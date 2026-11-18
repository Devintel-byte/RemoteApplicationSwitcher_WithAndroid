package com.movingsurface.showcontrol.data

import okhttp3.MediaType.Companion.toMediaType
import okhttp3.OkHttpClient
import okhttp3.Request
import okhttp3.RequestBody.Companion.toRequestBody
import java.io.IOException
import java.net.URI
import java.net.URLEncoder
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import kotlinx.serialization.Serializable
import kotlinx.serialization.json.buildJsonObject
import kotlinx.serialization.json.contentOrNull
import kotlinx.serialization.json.jsonObject
import kotlinx.serialization.json.jsonPrimitive
import kotlinx.serialization.json.put

@Serializable
data class HubUser(
    val id: String = "",
    val username: String = "",
    val displayName: String = "",
    val role: String = "operator",
)

@Serializable
data class HubWorkspace(
    val name: String = "Show Control",
    val accent: String = "indigo",
    val timeZone: String? = null,
    /** Relative to the hub address, e.g. /api/workspace/logo?v=3. Null when there's no logo. */
    val logoUrl: String? = null,
)

@Serializable
data class HubAgent(
    val agentId: String,
    val name: String,
    val hostname: String? = null,
    val online: Boolean = false,
    val version: String? = null,
    val apps: Int = 0,
    val running: Int = 0,
    val crashed: Int = 0,
    val canWake: Boolean = false,
)

/** A room someone published to the hub (GET /api/rooms). */
@Serializable
data class HubSharedRoom(
    val id: String,
    val name: String,
    val kind: String = "",
    val tiles: Int = 0,
    val pcs: Int = 0,
    val updatedBy: String = "",
    val updatedAt: String = "",
)

@Serializable
private data class SharedRoomsReply(val rooms: List<HubSharedRoom> = emptyList())

@Serializable
private data class SharedRoomReply(val pack: RoomPack)

@Serializable
private data class LoginReply(val token: String, val user: HubUser, val workspace: HubWorkspace)

@Serializable
private data class MeReply(val user: HubUser, val workspace: HubWorkspace)

@Serializable
private data class AgentsReply(val agents: List<HubAgent> = emptyList())

data class HubLogin(val token: String, val user: HubUser, val workspace: HubWorkspace)

/** [signedOut] = the hub no longer accepts this phone's sign-in. */
class HubException(message: String, val signedOut: Boolean = false) : Exception(message)

/** The hub's REST API (see hub/ShowControl.Hub/HubApi.cs). Phones control PCs over /ws/agent/{id}. */
object HubClient {

    private val JsonType = "application/json; charset=utf-8".toMediaType()

    /** "hub.example.com" → https://hub.example.com ; "192.168.1.10:47900" → http://192.168.1.10:47900 */
    fun normalizeUrl(input: String): String {
        var s = input.trim().trimEnd('/')
        if (s.endsWith("/api")) s = s.removeSuffix("/api")
        if (!s.contains("://")) {
            val hostPart = s.substringBefore('/').substringBefore(':')
            val local = hostPart == "localhost" || hostPart.all { it.isDigit() || it == '.' } || s.substringBefore('/').contains(':')
            s = (if (local) "http://" else "https://") + s
        }
        return s
    }

    /** Address shown for a hub PC, e.g. "hub.example.com" and 443. */
    fun hostAndPort(url: String): Pair<String, Int> = try {
        val u = URI(url)
        val port = if (u.port > 0) u.port else if (u.scheme.equals("https", true)) 443 else 80
        (u.host ?: url) to port
    } catch (e: Exception) {
        url to 443
    }

    fun socketUrl(baseUrl: String, agentId: String): String {
        val ws = when {
            baseUrl.startsWith("https://", true) -> "wss://" + baseUrl.substring(8)
            baseUrl.startsWith("http://", true) -> "ws://" + baseUrl.substring(7)
            else -> baseUrl
        }
        return ws.trimEnd('/') + "/ws/agent/" + URLEncoder.encode(agentId, "UTF-8")
    }

    suspend fun login(http: OkHttpClient, url: String, username: String, password: String, device: String): HubLogin {
        val body = buildJsonObject {
            put("username", username.trim())
            put("password", password)
            put("device", device)
        }.toString()
        val text = call(http, Request.Builder().url("$url/api/login").post(body.toRequestBody(JsonType)).build(), loginCall = true)
        val r = AppJson.decodeFromString(LoginReply.serializer(), text)
        return HubLogin(r.token, r.user, r.workspace)
    }

    suspend fun me(http: OkHttpClient, account: HubAccount): Pair<HubUser, HubWorkspace> {
        val r = AppJson.decodeFromString(MeReply.serializer(), call(http, authed(account, "/api/me").get().build()))
        return r.user to r.workspace
    }

    suspend fun agents(http: OkHttpClient, account: HubAccount): List<HubAgent> =
        AppJson.decodeFromString(AgentsReply.serializer(), call(http, authed(account, "/api/agents").get().build())).agents

    suspend fun wake(http: OkHttpClient, account: HubAccount, agentId: String) {
        call(http, authed(account, "/api/agents/" + URLEncoder.encode(agentId, "UTF-8") + "/wake").post(ByteArray(0).toRequestBody()).build())
    }

    suspend fun sharedRooms(http: OkHttpClient, account: HubAccount): List<HubSharedRoom> =
        AppJson.decodeFromString(SharedRoomsReply.serializer(), call(http, authed(account, "/api/rooms").get().build())).rooms

    suspend fun sharedRoom(http: OkHttpClient, account: HubAccount, id: String): RoomPack {
        val text = call(http, authed(account, "/api/rooms/" + URLEncoder.encode(id, "UTF-8")).get().build())
        val pack = try { AppJson.decodeFromString(SharedRoomReply.serializer(), text).pack } catch (e: Exception) {
            throw HubException("That shared room couldn't be read. Update Show Control.")
        }
        return RoomShare.parse(AppJson.encodeToString(RoomPack.serializer(), pack))
    }

    /** Publishes (or updates) a room on the hub so other phones in the workspace can import it. */
    suspend fun publishRoom(http: OkHttpClient, account: HubAccount, pack: RoomPack) {
        val body = buildJsonObject { put("pack", AppJson.encodeToJsonElement(RoomPack.serializer(), pack)) }.toString()
        call(http, authed(account, "/api/rooms/" + URLEncoder.encode(pack.room.id, "UTF-8")).put(body.toRequestBody(JsonType)).build())
    }

    suspend fun deleteSharedRoom(http: OkHttpClient, account: HubAccount, id: String) {
        call(http, authed(account, "/api/rooms/" + URLEncoder.encode(id, "UTF-8")).delete().build())
    }

    suspend fun logout(http: OkHttpClient, account: HubAccount) {
        runCatching { call(http, authed(account, "/api/logout").post(ByteArray(0).toRequestBody()).build()) }
    }

    /** Downloads the workspace logo. Null when there is none or it can't be fetched. */
    suspend fun downloadLogo(http: OkHttpClient, account: HubAccount, logoUrl: String?): ByteArray? {
        if (logoUrl.isNullOrBlank()) return null
        val full = if (logoUrl.startsWith("http")) logoUrl else account.url + logoUrl
        return withContext(Dispatchers.IO) {
            runCatching {
                http.newCall(Request.Builder().url(full).build()).execute().use { res ->
                    if (res.isSuccessful) res.body?.bytes() else null
                }
            }.getOrNull()
        }
    }

    private fun authed(account: HubAccount, path: String): Request.Builder =
        Request.Builder().url(account.url + path).header("Authorization", "Bearer ${account.token}")

    private suspend fun call(http: OkHttpClient, request: Request, loginCall: Boolean = false): String = withContext(Dispatchers.IO) {
        try {
            http.newCall(request).execute().use { res ->
                val text = res.body?.string().orEmpty()
                if (res.isSuccessful) return@use text
                val message = errorMessage(text)
                when (res.code) {
                    401 -> throw HubException(if (loginCall) message ?: "Wrong username or password" else "Your hub sign-in has expired. Sign in again.", signedOut = !loginCall)
                    403 -> throw HubException(message ?: "You don't have permission to do that")
                    404 -> throw HubException(if (loginCall) "No Show Control hub at this address" else message ?: "Not found")
                    429 -> throw HubException(message ?: "Too many attempts. Wait a few minutes.")
                    else -> throw HubException(message ?: "The hub answered ${res.code}")
                }
            }
        } catch (e: IOException) {
            throw HubException("Can't reach the hub at ${request.url.host}. Check the address and your internet connection.")
        } catch (e: IllegalArgumentException) {
            throw HubException("That doesn't look like a hub address")
        }
    }

    private fun errorMessage(text: String): String? = runCatching {
        val o = AppJson.parseToJsonElement(text).jsonObject
        (o["error"] ?: o["detail"] ?: o["title"])?.jsonPrimitive?.contentOrNull
    }.getOrNull()
}
