package com.movingsurface.showcontrol.data

import android.content.Context
import android.util.Log
import androidx.datastore.core.DataStore
import androidx.datastore.preferences.core.Preferences
import androidx.datastore.preferences.core.edit
import androidx.datastore.preferences.core.stringPreferencesKey
import androidx.datastore.preferences.preferencesDataStore
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.map

private val Context.dataStore: DataStore<Preferences> by preferencesDataStore(name = "show_control")

/** Rooms, paired PCs, theme and branding, stored as one JSON document on the phone. */
class SettingsRepository(private val context: Context) {

    private val key = stringPreferencesKey("settings_v1")

    val settings: Flow<AppSettings> = context.dataStore.data.map { decode(it[key]) }

    suspend fun update(transform: (AppSettings) -> AppSettings) {
        context.dataStore.edit { prefs ->
            val current = decode(prefs[key])
            prefs[key] = AppJson.encodeToString(AppSettings.serializer(), transform(current))
        }
    }

    private fun decode(raw: String?): AppSettings {
        if (raw.isNullOrBlank()) return AppSettings()
        return try {
            migrate(AppJson.decodeFromString(AppSettings.serializer(), raw))
        } catch (e: Exception) {
            Log.e("ShowControl", "Settings unreadable, starting fresh", e)
            AppSettings()
        }
    }

    /** One-off upgrades of settings saved by older versions. */
    private fun migrate(s: AppSettings): AppSettings {
        var out = s
        // 1.5: the brand colour moved from amber to indigo (the app icon). Keep any other colour the user chose.
        if (out.settingsVersion < 2) out = out.copy(accent = if (out.accent == "amber") "indigo" else out.accent, settingsVersion = 2)
        return out
    }
}
