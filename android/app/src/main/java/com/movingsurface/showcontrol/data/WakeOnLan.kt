package com.movingsurface.showcontrol.data

import java.net.DatagramPacket
import java.net.DatagramSocket
import java.net.InetAddress
import java.net.NetworkInterface
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext

object WakeOnLan {

    fun isValidMac(mac: String?): Boolean = mac?.count { it.isLetterOrDigit() && it.lowercaseChar() in "0123456789abcdef" } == 12

    private fun macBytes(mac: String): ByteArray {
        val hex = mac.filter { it.lowercaseChar() in "0123456789abcdef" }
        require(hex.length == 12) { "'$mac' is not a MAC address" }
        return ByteArray(6) { i -> hex.substring(i * 2, i * 2 + 2).toInt(16).toByte() }
    }

    /** Sends the magic packet from the phone to every broadcast address it can see (ports 9 and 7). */
    suspend fun send(mac: String) = withContext(Dispatchers.IO) {
        val m = macBytes(mac)
        val packet = ByteArray(102)
        for (i in 0 until 6) packet[i] = 0xFF.toByte()
        for (i in 0 until 16) System.arraycopy(m, 0, packet, 6 + i * 6, 6)

        val targets = mutableSetOf<InetAddress>(InetAddress.getByName("255.255.255.255"))
        runCatching {
            for (ni in NetworkInterface.getNetworkInterfaces()) {
                if (!ni.isUp || ni.isLoopback) continue
                ni.interfaceAddresses.mapNotNullTo(targets) { it.broadcast }
            }
        }
        DatagramSocket().use { socket ->
            socket.broadcast = true
            for (addr in targets) for (port in intArrayOf(9, 7)) {
                runCatching { socket.send(DatagramPacket(packet, packet.size, addr, port)) }
            }
        }
    }
}
