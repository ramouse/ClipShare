package com.clipshare.platform.android

import android.content.ContentResolver
import android.database.Cursor
import android.net.Uri
import android.provider.OpenableColumns
import com.clipshare.core.model.InputStreamFactory
import com.clipshare.core.model.OutputStreamFactory
import com.clipshare.core.model.ReadableDocument
import com.clipshare.core.model.WritableDocument
import java.io.FileNotFoundException

class AndroidDocumentResolver(private val contentResolver: ContentResolver) {
    fun readable(uri: Uri): ReadableDocument {
        val metadata = queryMetadata(uri)
        return ReadableDocument(
            displayName = metadata.displayName,
            contentType = contentResolver.getType(uri) ?: "application/octet-stream",
            sizeBytes = metadata.sizeBytes,
            inputStreamFactory = InputStreamFactory {
                contentResolver.openInputStream(uri)
                    ?: throw FileNotFoundException("ContentResolver returned no input stream")
            },
        )
    }

    fun writable(uri: Uri): WritableDocument = WritableDocument(
        OutputStreamFactory {
            contentResolver.openOutputStream(uri, "w")
                ?: throw FileNotFoundException("ContentResolver returned no output stream")
        },
    )

    private fun queryMetadata(uri: Uri): DocumentMetadata {
        val cursor = contentResolver.query(
            uri,
            arrayOf(OpenableColumns.DISPLAY_NAME, OpenableColumns.SIZE),
            null,
            null,
            null,
        )
        return cursor?.use {
            if (it.moveToFirst()) {
                val name = it.stringOrNull(OpenableColumns.DISPLAY_NAME)?.takeIf(String::isNotBlank)
                    ?: "selected-file"
                val size = it.longOrNull(OpenableColumns.SIZE)?.takeIf { value -> value >= 0 }
                DocumentMetadata(name, size)
            } else {
                DocumentMetadata("selected-file", null)
            }
        } ?: DocumentMetadata("selected-file", null)
    }

    private fun Cursor.stringOrNull(column: String): String? {
        val index = getColumnIndex(column)
        return if (index < 0 || isNull(index)) null else getString(index)
    }

    private fun Cursor.longOrNull(column: String): Long? {
        val index = getColumnIndex(column)
        return if (index < 0 || isNull(index)) null else getLong(index)
    }

    private data class DocumentMetadata(val displayName: String, val sizeBytes: Long?)
}
