package io.github.rzukic.usagetop.net

import okhttp3.MediaType.Companion.toMediaType
import okhttp3.OkHttpClient
import okhttp3.Request
import okhttp3.RequestBody.Companion.toRequestBody
import java.util.concurrent.TimeUnit

data class HttpResponse(val code: Int, val body: String)

object Http {

    private val client: OkHttpClient = OkHttpClient.Builder()
        .connectTimeout(15, TimeUnit.SECONDS)
        .readTimeout(20, TimeUnit.SECONDS)
        .callTimeout(30, TimeUnit.SECONDS)
        .build()

    fun get(url: String, headers: Map<String, String>): HttpResponse {
        val request = Request.Builder()
            .url(url)
            .get()
            .apply { headers.forEach { (name, value) -> header(name, value) } }
            .build()

        client.newCall(request).execute().use { response ->
            return HttpResponse(response.code, response.body?.string().orEmpty())
        }
    }

    fun postJson(url: String, headers: Map<String, String>, body: String): HttpResponse =
        send(Request.Builder().url(url).post(body.toRequestBody(JSON)), headers)

    fun putJson(url: String, headers: Map<String, String>, body: String): HttpResponse =
        send(Request.Builder().url(url).put(body.toRequestBody(JSON)), headers)

    fun patchJson(url: String, headers: Map<String, String>, body: String): HttpResponse =
        send(Request.Builder().url(url).patch(body.toRequestBody(JSON)), headers)

    fun delete(url: String, headers: Map<String, String>): HttpResponse =
        send(Request.Builder().url(url).delete(), headers)

    fun postForm(url: String, headers: Map<String, String>, body: String): HttpResponse =
        send(Request.Builder().url(url).post(body.toRequestBody(FORM)), headers)

    private fun send(builder: Request.Builder, headers: Map<String, String>): HttpResponse {
        val request = builder
            .apply { headers.forEach { (name, value) -> header(name, value) } }
            .build()

        client.newCall(request).execute().use { response ->
            return HttpResponse(response.code, response.body?.string().orEmpty())
        }
    }

    private val JSON = "application/json; charset=utf-8".toMediaType()
    private val FORM = "application/x-www-form-urlencoded".toMediaType()
}
