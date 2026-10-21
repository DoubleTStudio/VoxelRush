package com.doubletstudio.voxelrush.security;

import android.content.Context;
import android.content.SharedPreferences;
import android.security.keystore.KeyGenParameterSpec;
import android.security.keystore.KeyProperties;
import android.util.Base64;

import java.nio.charset.StandardCharsets;
import java.security.KeyStore;

import javax.crypto.Cipher;
import javax.crypto.KeyGenerator;
import javax.crypto.SecretKey;
import javax.crypto.spec.GCMParameterSpec;

/** AUTH-002 refresh token: AES-GCM key in AndroidKeyStore, ciphertext in private SharedPreferences. */
public final class SecureTokenStorage {
    private static final String KEYSTORE = "AndroidKeyStore";
    private static final String ALIAS = "com.doubletstudio.voxelrush.refresh";
    private static final String PREFS = "voxelrush.secure";
    private static final String TRANSFORMATION = "AES/GCM/NoPadding";
    private static final int TAG_BITS = 128;

    private final SharedPreferences prefs;

    public SecureTokenStorage(Context context) {
        prefs = context.getApplicationContext().getSharedPreferences(PREFS, Context.MODE_PRIVATE);
    }

    /** The stored value, or null when missing or undecryptable. */
    public String read(String key) {
        String stored = prefs.getString(key, null);
        if (stored == null) {
            return null;
        }

        try {
            SecretKey secret = existingKey();
            byte[] blob = Base64.decode(stored, Base64.NO_WRAP);
            int ivLength = blob.length > 0 ? blob[0] : 0;
            if (secret == null || ivLength <= 0 || blob.length <= 1 + ivLength) {
                throw new IllegalStateException("unreadable");
            }

            Cipher cipher = Cipher.getInstance(TRANSFORMATION);
            cipher.init(Cipher.DECRYPT_MODE, secret, new GCMParameterSpec(TAG_BITS, blob, 1, ivLength));
            byte[] plain = cipher.doFinal(blob, 1 + ivLength, blob.length - 1 - ivLength);
            return new String(plain, StandardCharsets.UTF_8);
        } catch (Exception e) {
            // Key gone (backup restored to another device, keystore reset) or data damaged: treat as no token.
            prefs.edit().remove(key).commit();
            return null;
        }
    }

    public boolean write(String key, String value) {
        try {
            Cipher cipher = Cipher.getInstance(TRANSFORMATION);
            // The keystore picks a random IV; supplying one is refused for these keys.
            cipher.init(Cipher.ENCRYPT_MODE, getOrCreateKey());
            byte[] iv = cipher.getIV();
            byte[] sealed = cipher.doFinal(value.getBytes(StandardCharsets.UTF_8));
            byte[] blob = new byte[1 + iv.length + sealed.length];
            blob[0] = (byte) iv.length;
            System.arraycopy(iv, 0, blob, 1, iv.length);
            System.arraycopy(sealed, 0, blob, 1 + iv.length, sealed.length);
            return prefs.edit().putString(key, Base64.encodeToString(blob, Base64.NO_WRAP)).commit();
        } catch (Exception e) {
            return false;
        }
    }

    public void delete(String key) {
        prefs.edit().remove(key).commit();
    }

    private static SecretKey existingKey() throws Exception {
        KeyStore store = KeyStore.getInstance(KEYSTORE);
        store.load(null);
        return (SecretKey) store.getKey(ALIAS, null);
    }

    private static SecretKey getOrCreateKey() throws Exception {
        try {
            SecretKey existing = existingKey();
            if (existing != null) {
                return existing;
            }
        } catch (Exception e) {
            // A damaged entry would fail every write; replace it (old ciphertext was unreadable anyway).
            KeyStore store = KeyStore.getInstance(KEYSTORE);
            store.load(null);
            store.deleteEntry(ALIAS);
        }

        KeyGenerator generator = KeyGenerator.getInstance(KeyProperties.KEY_ALGORITHM_AES, KEYSTORE);
        generator.init(new KeyGenParameterSpec.Builder(ALIAS, KeyProperties.PURPOSE_ENCRYPT | KeyProperties.PURPOSE_DECRYPT)
                .setBlockModes(KeyProperties.BLOCK_MODE_GCM)
                .setEncryptionPaddings(KeyProperties.ENCRYPTION_PADDING_NONE)
                .setKeySize(256)
                .build());
        return generator.generateKey();
    }
}
