package com.clipshare.android;

import android.content.ContentProvider;
import android.content.ContentValues;
import android.content.Context;
import android.database.Cursor;
import android.database.MatrixCursor;
import android.net.Uri;
import android.os.ParcelFileDescriptor;
import android.provider.OpenableColumns;

import java.io.File;
import java.io.FileNotFoundException;
import java.io.FileOutputStream;
import java.io.IOException;
import java.nio.charset.StandardCharsets;

public final class TestDocumentProvider extends ContentProvider {
    private static final String INPUT_PATH = "input";
    private static final String OUTPUT_PATH = "output";
    private static final String INPUT_FILE = "a1-provider-input-v3.bin";
    private static final String OUTPUT_FILE = "a1-provider-output-v3.bin";
    private static final byte[] DOCUMENT_CONTENT =
            "streamed through content resolver".getBytes(StandardCharsets.UTF_8);

    @Override
    public boolean onCreate() {
        return true;
    }

    @Override
    public Cursor query(
            Uri uri,
            String[] projection,
            String selection,
            String[] selectionArgs,
            String sortOrder
    ) {
        File file = fileFor(uri);
        ensureInputFixture(uri, file);
        MatrixCursor cursor = new MatrixCursor(
                new String[]{OpenableColumns.DISPLAY_NAME, OpenableColumns.SIZE}
        );
        cursor.addRow(new Object[]{"fixture.bin", file.exists() ? file.length() : null});
        return cursor;
    }

    @Override
    public String getType(Uri uri) {
        return "application/octet-stream";
    }

    @Override
    public ParcelFileDescriptor openFile(Uri uri, String mode) throws FileNotFoundException {
        File file = fileFor(uri);
        ensureInputFixture(uri, file);
        int flags = mode.indexOf('w') >= 0
                ? ParcelFileDescriptor.MODE_CREATE
                    | ParcelFileDescriptor.MODE_TRUNCATE
                    | ParcelFileDescriptor.MODE_WRITE_ONLY
                : ParcelFileDescriptor.MODE_READ_ONLY;
        return ParcelFileDescriptor.open(file, flags);
    }

    @Override
    public Uri insert(Uri uri, ContentValues values) {
        return null;
    }

    @Override
    public int delete(Uri uri, String selection, String[] selectionArgs) {
        return fileFor(uri).delete() ? 1 : 0;
    }

    @Override
    public int update(Uri uri, ContentValues values, String selection, String[] selectionArgs) {
        return 0;
    }

    private File fileFor(Uri uri) {
        String path = uri.getLastPathSegment();
        final String name;
        if (INPUT_PATH.equals(path)) {
            name = INPUT_FILE;
        } else if (OUTPUT_PATH.equals(path)) {
            name = OUTPUT_FILE;
        } else {
            throw new IllegalArgumentException("Unknown test document URI: " + uri);
        }
        Context context = getContext();
        if (context == null) {
            throw new IllegalStateException("Test provider is not attached to a context");
        }
        return new File(context.getCacheDir(), name);
    }

    private void ensureInputFixture(Uri uri, File file) throws IllegalStateException {
        if (!INPUT_PATH.equals(uri.getLastPathSegment()) || file.exists()) {
            return;
        }
        try (FileOutputStream output = new FileOutputStream(file)) {
            output.write(DOCUMENT_CONTENT);
        } catch (IOException error) {
            throw new IllegalStateException("Could not create test document fixture", error);
        }
    }
}
