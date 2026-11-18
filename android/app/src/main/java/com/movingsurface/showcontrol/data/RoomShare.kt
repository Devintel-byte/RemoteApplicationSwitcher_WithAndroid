package com.movingsurface.showcontrol.data

import java.io.ByteArrayOutputStream
import java.util.Base64
import java.util.zip.Deflater
import java.util.zip.Inflater
import kotlinx.serialization.Serializable

/** A PC a shared room uses. Never carries a pairing token or hub key: the receiving phone pairs on its own. */
@Serializable
data class SharedPcRef(
    /** The PC's id on the sending phone; tile steps refer to it. */
    val ref: String,
    val name: String,
    val agentId: String? = null,
    val hubAgentId: String? = null,
    val host: String? = null,
    val port: Int = 47800,
)

@Serializable
data class SharedRoomInfo(val id: String, val name: String, val kind: RoomKind = RoomKind.PRODUCTION)

/** Everything another phone needs to rebuild a room's tiles. Sent as a share code or published to the hub. */
@Serializable
data class RoomPack(
    val v: Int = 1,
    val room: SharedRoomInfo,
    val pcs: List<SharedPcRef> = emptyList(),
    val tiles: List<ShowScene> = emptyList(),
    val from: String = "",
    val at: Long = System.currentTimeMillis(),
)

/** What importing a pack would do (shown before the user confirms) and the settings that result. */
data class RoomImport(
    val settings: AppSettings,
    val roomId: String,
    val roomName: String,
    val tiles: Int,
    /** True when this phone already has the room (same id): its tiles are replaced. */
    val updatesExisting: Boolean,
    /** PCs this phone already knows (paired or through the hub). */
    val ready: List<String>,
    /** PCs that need their pairing PIN before the tiles can use them. */
    val needPin: List<String>,
    /** Hub PCs that work once this phone signs in to the hub. */
    val needHub: List<String>,
)

object RoomShare {

    const val PREFIX = "SHOWCONTROL-ROOM:1:"
    private const val MAX_BYTES = 1_000_000

    fun build(s: AppSettings, roomId: String, from: String): RoomPack {
        val room = s.rooms.first { it.id == roomId }
        val tiles = s.scenesIn(roomId)
        val usedIds = s.pcsIn(roomId).map { it.id }.toSet() + tiles.flatMap { t -> t.steps.mapNotNull { it.pcId } }
        val pcs = s.pcs.filter { it.id in usedIds }.map { pc ->
            SharedPcRef(
                ref = pc.id, name = pc.name, agentId = pc.agentId, hubAgentId = pc.hubAgentId,
                host = if (pc.viaHub) null else pc.host, port = pc.port,
            )
        }
        return RoomPack(room = SharedRoomInfo(room.id, room.name, room.kind), pcs = pcs, tiles = tiles, from = from)
    }

    /** "SHOWCONTROL-ROOM:1:" + deflated JSON in URL-safe base64: survives WhatsApp, email and copy-paste. */
    fun encode(pack: RoomPack): String {
        val json = AppJson.encodeToString(RoomPack.serializer(), pack).toByteArray(Charsets.UTF_8)
        val deflater = Deflater(Deflater.BEST_COMPRESSION).apply { setInput(json); finish() }
        val out = ByteArrayOutputStream()
        val buf = ByteArray(8192)
        while (!deflater.finished()) out.write(buf, 0, deflater.deflate(buf))
        deflater.end()
        return PREFIX + Base64.getUrlEncoder().withoutPadding().encodeToString(out.toByteArray())
    }

    /** Finds a share code anywhere in [text] (a forwarded message has words around it). */
    fun containsCode(text: String?): Boolean = text?.contains(PREFIX) == true

    /** Throws IllegalArgumentException with a message to show when the text isn't a usable code. */
    fun decode(text: String): RoomPack {
        val start = text.indexOf(PREFIX)
        require(start >= 0) { "That isn't a Show Control room code. It starts with $PREFIX" }
        val body = text.substring(start + PREFIX.length).takeWhile { it.isLetterOrDigit() || it == '-' || it == '_' }
        val packed = try { Base64.getUrlDecoder().decode(body) } catch (e: IllegalArgumentException) {
            throw IllegalArgumentException("The room code is damaged. Copy the whole message again.")
        }
        val inflater = Inflater().apply { setInput(packed) }
        val out = ByteArrayOutputStream()
        val buf = ByteArray(8192)
        try {
            while (!inflater.finished()) {
                val n = inflater.inflate(buf)
                if (n == 0 && (inflater.needsInput() || inflater.needsDictionary())) break
                out.write(buf, 0, n)
                require(out.size() <= MAX_BYTES) { "That room code is too big" }
            }
        } catch (e: java.util.zip.DataFormatException) {
            throw IllegalArgumentException("The room code is damaged or incomplete. Copy the whole message again.")
        } finally {
            inflater.end()
        }
        return parse(out.toString(Charsets.UTF_8.name()))
    }

    fun parse(json: String): RoomPack {
        val pack = try { AppJson.decodeFromString(RoomPack.serializer(), json) } catch (e: Exception) {
            throw IllegalArgumentException("The room code couldn't be read. Update Show Control on both phones and share it again.")
        }
        require(pack.v == 1) { "This room was shared from a newer version of Show Control. Update the app." }
        require(pack.room.name.isNotBlank()) { "The shared room has no name" }
        return pack
    }

    /**
     * Merges [pack] into [s]. The room keeps its id, so importing again (or from the hub after it was
     * republished) updates the same room instead of making a copy. PCs are matched by agent id: one this
     * phone already has is reused; a hub PC is added through the signed-in hub; any other PC is added
     * without a key and asks for its pairing PIN.
     */
    fun merge(s: AppSettings, pack: RoomPack): RoomImport {
        val roomId = pack.room.id
        val existing = s.rooms.firstOrNull { it.id == roomId }
        val room = (existing ?: Room(id = roomId, name = pack.room.name, kind = pack.room.kind))
            .copy(name = pack.room.name, kind = pack.room.kind)

        var pcs = s.pcs
        val map = mutableMapOf<String, String>()
        val ready = mutableListOf<String>()
        val needPin = mutableListOf<String>()
        val needHub = mutableListOf<String>()

        for (ref in pack.pcs) {
            val inRoom = pcs.firstOrNull { pc ->
                pc.roomId == roomId && ((ref.hubAgentId != null && pc.hubAgentId == ref.hubAgentId) ||
                    (ref.hubAgentId == null && ref.agentId != null && pc.agentId == ref.agentId))
            }
            if (inRoom != null) {
                map[ref.ref] = inRoom.id
                if (inRoom.viaHub && s.hub == null) needHub += inRoom.name
                else if (!inRoom.viaHub && inRoom.token.isBlank()) needPin += inRoom.name
                else ready += inRoom.name
                continue
            }
            val added: PcEndpoint = when {
                ref.hubAgentId != null -> {
                    val (host, port) = s.hub?.let { HubClient.hostAndPort(it.url) } ?: ("hub" to 443)
                    if (s.hub == null) needHub += ref.name else ready += ref.name
                    PcEndpoint(roomId = roomId, name = ref.name, host = host, port = port, token = "", hubAgentId = ref.hubAgentId)
                }
                else -> {
                    val known = ref.agentId?.let { id -> pcs.firstOrNull { !it.viaHub && it.agentId == id && it.token.isNotBlank() } }
                    if (known != null) {
                        ready += ref.name
                        known.copy(id = newId(), roomId = roomId, name = ref.name)
                    } else {
                        needPin += ref.name
                        PcEndpoint(roomId = roomId, name = ref.name, host = ref.host ?: "", port = ref.port, token = "", agentId = ref.agentId)
                    }
                }
            }
            pcs = pcs + added
            map[ref.ref] = added.id
        }

        val tiles = pack.tiles.map { t ->
            t.copy(roomId = roomId, steps = t.steps.map { st -> st.copy(pcId = st.pcId?.let { map[it] }) })
        }
        val otherTiles = s.scenes.filterNot { it.roomId == roomId }
        val rooms = if (existing != null) s.rooms.map { if (it.id == roomId) room else it } else s.rooms + room

        return RoomImport(
            settings = s.copy(rooms = rooms, pcs = pcs, scenes = otherTiles + tiles),
            roomId = roomId,
            roomName = room.name,
            tiles = tiles.size,
            updatesExisting = existing != null,
            ready = ready,
            needPin = needPin,
            needHub = needHub,
        )
    }
}
