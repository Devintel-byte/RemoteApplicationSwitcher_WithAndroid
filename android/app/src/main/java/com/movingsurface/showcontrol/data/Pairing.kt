package com.movingsurface.showcontrol.data

import okhttp3.OkHttpClient
import okhttp3.Request
import okhttp3.Response
import okhttp3.WebSocket
import okhttp3.WebSocketListener
import java.util.concurrent.atomic.AtomicBoolean
import kotlin.coroutines.resume
import kotlin.coroutines.resumeWithException
import kotlinx.coroutines.TimeoutCancellationException
import kotlinx.coroutines.suspendCancellableCoroutine
import kotlinx.coroutines.withTimeout
import kotlinx.serialization.json.buildJsonObject
import kotlinx.serialization.json.contentOrNull
import kotlinx.serialization.json.jsonObject
import kotlinx.serialization.json.jsonPrimitive
import kotlinx.serialization.json.put

data class PairResult(val token: String, val agentName: String, val agentId: String)

class PairException(message: String) : Exception(message)

/** Exchanges the 6-digit PIN for a long-lived token, then confirms it with a hello. */
suspend fun pairWithAgent(http: OkHttpClient, host: String, port: Int, pin: String, clientName: String): PairResult {
    val request = try {
        Request.Builder().url("ws://$host:$port/ws").build()
    } catch (e: IllegalArgumentException) {
        throw PairException("\"$host\" is not a valid address")
    }

    return try {
        withTimeout(10_000) {
            suspendCancellableCoroutine { cont ->
                val done = AtomicBoolean(false)
                var token: String? = null

                fun finish(result: Result<PairResult>, ws: WebSocket) {
                    if (!done.compareAndSet(false, true)) return
                    ws.close(1000, null)
                    result.fold({ cont.resume(it) }, { cont.resumeWithException(it) })
                }

                val socket = http.newWebSocket(request, object : WebSocketListener() {
                    override fun onOpen(webSocket: WebSocket, response: Response) {
                        webSocket.send(buildJsonObject {
                            put("type", "pair"); put("id", "pair"); put("pin", pin); put("clientName", clientName)
                        }.toString())
                    }

                    override fun onMessage(webSocket: WebSocket, text: String) {
                        val msg = runCatching { AppJson.parseToJsonElement(text).jsonObject }.getOrNull() ?: return
                        when (msg["type"]?.jsonPrimitive?.contentOrNull) {
                            "paired" -> {
                                token = msg["token"]?.jsonPrimitive?.contentOrNull
                                webSocket.send(buildJsonObject {
                                    put("type", "hello"); put("id", "hello"); put("token", token ?: ""); put("clientName", clientName)
                                }.toString())
                            }
                            "welcome" -> {
                                val agent = msg["agent"]?.let { AppJson.decodeFromJsonElement(AgentInfo.serializer(), it) } ?: AgentInfo()
                                val t = token
                                if (t == null) finish(Result.failure(PairException("Agent did not return a token")), webSocket)
                                else finish(Result.success(PairResult(t, agent.name.ifBlank { host }, agent.agentId)), webSocket)
                            }
                            "error" -> {
                                val m = msg["message"]?.jsonPrimitive?.contentOrNull ?: "Pairing refused"
                                finish(Result.failure(PairException(m)), webSocket)
                            }
                        }
                    }

                    override fun onFailure(webSocket: WebSocket, t: Throwable, response: Response?) {
                        finish(Result.failure(PairException("Can't reach $host:$port. Is the agent running and the firewall open? (${t.message ?: "no answer"})")), webSocket)
                    }
                })
                cont.invokeOnCancellation { socket.cancel() }
            }
        }
    } catch (e: TimeoutCancellationException) {
        throw PairException("No answer from $host:$port")
    }
}
