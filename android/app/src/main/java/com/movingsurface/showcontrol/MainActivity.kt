package com.movingsurface.showcontrol

import android.Manifest
import android.content.Intent
import android.graphics.Color
import android.os.Build
import android.os.Bundle
import androidx.activity.ComponentActivity
import androidx.activity.SystemBarStyle
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.compose.setContent
import androidx.activity.enableEdgeToEdge
import androidx.activity.result.contract.ActivityResultContracts
import androidx.activity.viewModels
import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.navigationBarsPadding
import androidx.compose.foundation.layout.padding
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.SnackbarHost
import androidx.compose.material3.SnackbarHostState
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.remember
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.unit.dp
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import androidx.navigation.NavType
import androidx.navigation.compose.NavHost
import androidx.navigation.compose.composable
import androidx.navigation.compose.rememberNavController
import androidx.navigation.navArgument
import com.movingsurface.showcontrol.alerts.AlertNotifier
import com.movingsurface.showcontrol.alerts.MonitorService
import com.movingsurface.showcontrol.data.ThemeMode
import com.movingsurface.showcontrol.ui.MainViewModel
import com.movingsurface.showcontrol.ui.screens.AddPcScreen
import com.movingsurface.showcontrol.ui.screens.HubScreen
import com.movingsurface.showcontrol.ui.screens.ImportRoomScreen
import com.movingsurface.showcontrol.ui.screens.PcDetailScreen
import com.movingsurface.showcontrol.ui.screens.RoomScreen
import com.movingsurface.showcontrol.ui.screens.RoomsScreen
import com.movingsurface.showcontrol.ui.screens.SettingsScreen
import com.movingsurface.showcontrol.ui.screens.TileEditorScreen
import com.movingsurface.showcontrol.ui.theme.ShowControlTheme
import com.movingsurface.showcontrol.ui.theme.resolveDark

class MainActivity : ComponentActivity() {

    private val vm: MainViewModel by viewModels()

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        enableEdgeToEdge()
        if (savedInstanceState == null) handleIntent(intent)

        setContent {
            val settings by vm.settings.collectAsStateWithLifecycle()
            val s = settings
            val mode = s?.theme ?: ThemeMode.SYSTEM
            val dark = resolveDark(mode)

            // Status/nav bar icons follow the in-app theme, not just the system one.
            LaunchedEffect(dark) {
                val style = if (dark) SystemBarStyle.dark(Color.TRANSPARENT)
                else SystemBarStyle.light(Color.TRANSPARENT, Color.TRANSPARENT)
                enableEdgeToEdge(statusBarStyle = style, navigationBarStyle = style)
            }

            ShowControlTheme(mode = mode, accentKey = s?.accent ?: "indigo") {
                val snackbar = remember { SnackbarHostState() }
                LaunchedEffect(Unit) { vm.messages.collect { snackbar.showSnackbar(it) } }

                // Crash alerts need the notification permission on Android 13+: ask once, when there's a PC to watch.
                val askNotifications = rememberLauncherForActivityResult(ActivityResultContracts.RequestPermission()) { }
                LaunchedEffect(s?.pcs?.isNotEmpty(), s?.askedNotifications) {
                    if (s != null && s.pcs.isNotEmpty() && !s.askedNotifications && Build.VERSION.SDK_INT >= 33 &&
                        (s.alerts.crashes || s.alerts.offline) && !vm.notificationsPermitted()
                    ) {
                        vm.markAskedNotifications()
                        askNotifications.launch(Manifest.permission.POST_NOTIFICATIONS)
                    }
                }
                // Background monitoring is started from here, while the app is on screen (Android requires that).
                LaunchedEffect(s?.alerts?.background) {
                    when (s?.alerts?.background) {
                        true -> MonitorService.start(this@MainActivity)
                        false -> MonitorService.stop(this@MainActivity)
                        null -> {}
                    }
                }

                Box(Modifier.fillMaxSize().background(MaterialTheme.colorScheme.background)) {
                    if (s != null) {
                        val nav = rememberNavController()
                        val openPc by vm.openPc.collectAsStateWithLifecycle()
                        val incoming by vm.incomingCode.collectAsStateWithLifecycle()
                        LaunchedEffect(openPc) {
                            val id = openPc ?: return@LaunchedEffect
                            vm.consumeOpenPc()
                            if (s.pcs.any { it.id == id }) nav.navigate("pc/$id") { launchSingleTop = true }
                        }
                        LaunchedEffect(incoming) {
                            if (incoming != null && nav.currentDestination?.route != "import") nav.navigate("import") { launchSingleTop = true }
                        }
                        NavHost(navController = nav, startDestination = "rooms") {
                            composable("rooms") {
                                RoomsScreen(
                                    vm = vm,
                                    settings = s,
                                    onOpenRoom = { nav.navigate("room/$it") },
                                    onOpenSettings = { nav.navigate("settings") },
                                    onImport = { nav.navigate("import") },
                                    onOpenHub = { nav.navigate("hub") },
                                )
                            }
                            composable("room/{roomId}", arguments = listOf(navArgument("roomId") { type = NavType.StringType })) { entry ->
                                RoomScreen(
                                    vm = vm,
                                    settings = s,
                                    roomId = entry.arguments?.getString("roomId").orEmpty(),
                                    onBack = { nav.popBackStack() },
                                    onOpenPc = { nav.navigate("pc/$it") },
                                    onAddPc = { nav.navigate("addpc/$it") },
                                    onEditTile = { roomId, sceneId -> nav.navigate("tile/$roomId/${sceneId ?: "new"}") },
                                )
                            }
                            composable("pc/{pcId}", arguments = listOf(navArgument("pcId") { type = NavType.StringType })) { entry ->
                                PcDetailScreen(
                                    vm = vm,
                                    settings = s,
                                    pcId = entry.arguments?.getString("pcId").orEmpty(),
                                    onBack = { nav.popBackStack() },
                                )
                            }
                            composable("addpc/{roomId}", arguments = listOf(navArgument("roomId") { type = NavType.StringType })) { entry ->
                                AddPcScreen(
                                    vm = vm,
                                    settings = s,
                                    roomId = entry.arguments?.getString("roomId").orEmpty(),
                                    onBack = { nav.popBackStack() },
                                )
                            }
                            composable(
                                "tile/{roomId}/{sceneId}",
                                arguments = listOf(
                                    navArgument("roomId") { type = NavType.StringType },
                                    navArgument("sceneId") { type = NavType.StringType },
                                ),
                            ) { entry ->
                                TileEditorScreen(
                                    vm = vm,
                                    settings = s,
                                    roomId = entry.arguments?.getString("roomId").orEmpty(),
                                    sceneId = entry.arguments?.getString("sceneId")?.takeIf { it != "new" },
                                    onBack = { nav.popBackStack() },
                                )
                            }
                            composable("settings") {
                                SettingsScreen(vm = vm, settings = s, onBack = { nav.popBackStack() }, onOpenHub = { nav.navigate("hub") })
                            }
                            composable("hub") {
                                HubScreen(vm = vm, settings = s, onBack = { nav.popBackStack() })
                            }
                            composable("import") {
                                ImportRoomScreen(
                                    vm = vm,
                                    settings = s,
                                    incoming = incoming,
                                    onBack = { nav.popBackStack() },
                                    onImported = { roomId ->
                                        nav.navigate("room/$roomId") { popUpTo("rooms") }
                                    },
                                )
                            }
                        }
                    }
                    SnackbarHost(snackbar, Modifier.align(Alignment.BottomCenter).navigationBarsPadding().padding(bottom = 80.dp))
                }
            }
        }
    }

    override fun onNewIntent(intent: Intent) {
        super.onNewIntent(intent)
        handleIntent(intent)
    }

    /** A notification tap (open that PC) or a room code shared to Show Control from another app. */
    private fun handleIntent(intent: Intent?) {
        intent ?: return
        intent.getStringExtra(AlertNotifier.EXTRA_OPEN_PC)?.let { vm.requestOpenPc(it) }
        if (intent.action == Intent.ACTION_SEND) vm.receiveSharedText(intent.getStringExtra(Intent.EXTRA_TEXT))
    }
}
