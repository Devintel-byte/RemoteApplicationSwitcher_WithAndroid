package com.movingsurface.showcontrol.data

import android.content.Context
import android.graphics.Bitmap
import android.graphics.BitmapFactory
import android.net.Uri
import java.io.File
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext

/** The brand logo shown in the app header, stored as a PNG in the app's private files. */
object BrandLogo {

    private const val MAX_SIZE = 512

    fun file(context: Context): File = File(context.filesDir, "brand_logo.png")

    /** Decodes and saves an image (PNG, JPG, WebP). False if it isn't an image Android can read. */
    suspend fun save(context: Context, bytes: ByteArray): Boolean = withContext(Dispatchers.IO) {
        val bounds = BitmapFactory.Options().apply { inJustDecodeBounds = true }
        BitmapFactory.decodeByteArray(bytes, 0, bytes.size, bounds)
        if (bounds.outWidth <= 0 || bounds.outHeight <= 0) return@withContext false

        var sample = 1
        while (bounds.outWidth / (sample * 2) >= MAX_SIZE || bounds.outHeight / (sample * 2) >= MAX_SIZE) sample *= 2
        val decoded = BitmapFactory.decodeByteArray(bytes, 0, bytes.size, BitmapFactory.Options().apply { inSampleSize = sample })
            ?: return@withContext false

        val scale = minOf(1f, MAX_SIZE.toFloat() / maxOf(decoded.width, decoded.height))
        val bitmap = if (scale < 1f) {
            Bitmap.createScaledBitmap(decoded, (decoded.width * scale).toInt().coerceAtLeast(1), (decoded.height * scale).toInt().coerceAtLeast(1), true)
        } else decoded

        val tmp = File(context.filesDir, "brand_logo.tmp")
        tmp.outputStream().use { bitmap.compress(Bitmap.CompressFormat.PNG, 100, it) }
        tmp.renameTo(file(context))
    }

    suspend fun saveFromUri(context: Context, uri: Uri): Boolean = withContext(Dispatchers.IO) {
        val bytes = runCatching {
            context.contentResolver.openInputStream(uri)?.use { input ->
                // Logos are small; refuse anything absurd rather than running out of memory.
                val data = input.readBytes()
                if (data.size > 20 * 1024 * 1024) null else data
            }
        }.getOrNull() ?: return@withContext false
        save(context, bytes)
    }

    fun delete(context: Context) {
        file(context).delete()
    }

    suspend fun load(context: Context): Bitmap? = withContext(Dispatchers.IO) {
        val f = file(context)
        if (f.exists()) BitmapFactory.decodeFile(f.absolutePath) else null
    }
}
