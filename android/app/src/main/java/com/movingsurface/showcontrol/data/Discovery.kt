package com.movingsurface.showcontrol.data

import java.net.DatagramPacket
import java.net.DatagramSocket
import java.net.InetAddress
import java.net.NetworkInterface
import java.net.SocketTimeoutException
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import kotlinx.serialization.json.contentOrNull
import kotlinx.serialization.json.intOrNull
import kotlinx.serialization.json.jsonObject
import kotlinx.serialization.json.jsonPrimitive

data class DiscoveredAgent(
    val agentId: String,
    val name: String,
    val hostname: String,
    val host: String,
    val port: Int,
    val version: String,
)

private const val PROBE = "SHOWCTL_DISCOVER_V1"
private const val DISCOVERY_PORT = 47801

/** Broadcasts a probe on every network the phone is on and collects agent replies. */
suspend fun discoverAgents(timeoutMs: Long = 2_500): List<DiscoveredAgent> = withContext(Dispatchers.IO) {
    val found = linkedMapOf<String, DiscoveredAgent>()
    DatagramSocket().use { socket ->
        socket.broadcast = true
        socket.soTimeout = 250
        val probe = PROBE.toByteArray(Charsets.US_ASCII)
        val targets = broadcastAddresses()

        fun sendProbes() = targets.forEach { addr ->
            runCatching { socket.send(DatagramPacket(probe, probe.size, addr, DISCOVERY_PORT)) }
        }

        val start = System.currentTimeMillis()
        var resent = false
        sendProbes()
        val buffer = ByteArray(2048)
        while (System.currentTimeMillis() - start < timeoutMs) {
            if (!resent && System.currentTimeMillis() - start > timeoutMs / 3) { sendProbes(); resent = true }
            val packet = DatagramPacket(buffer, buffer.size)
            try {
                socket.receive(packet)
            } catch (e: SocketTimeoutException) {
                continue
            }
            val text = String(packet.data, 0, packet.length, Charsets.UTF_8)
            val obj = runCatching { AppJson.parseToJsonElement(text).jsonObject }.getOrNull() ?: continue
            if (obj["type"]?.jsonPrimitive?.contentOrNull != "agent_announce") continue
            val id = obj["agentId"]?.jsonPrimitive?.contentOrNull ?: continue
            found[id] = DiscoveredAgent(
                agentId = id,
                name = obj["name"]?.jsonPrimitive?.contentOrNull ?: "PC",
                hostname = obj["hostname"]?.jsonPrimitive?.contentOrNull ?: "",
                host = packet.address.hostAddress ?: continue,
                port = obj["port"]?.jsonPrimitive?.intOrNull ?: 47800,
                version = obj["version"]?.jsonPrimitive?.contentOrNull ?: "",
            )
        }
    }
    found.values.toList()
}

private fun broadcastAddresses(): List<InetAddress> {
    val list = mutableListOf<InetAddress>(InetAddress.getByName("255.255.255.255"))
    runCatching {
        for (ni in NetworkInterface.getNetworkInterfaces()) {
            if (!ni.isUp || ni.isLoopback) continue
            ni.interfaceAddresses.mapNotNullTo(list) { it.broadcast }
        }
    }
    return list.distinct()
}
