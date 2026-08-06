package com.xtool.collab.ui

import androidx.compose.foundation.isSystemInDarkTheme
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.lightColorScheme
import androidx.compose.runtime.Composable
import androidx.compose.ui.graphics.Color

private val LightColors = lightColorScheme(
    primary = Color(0xFF4D7CFE),
    onPrimary = Color.White,
    secondary = Color(0xFF16B99B),
    background = Color(0xFFEEF6FB),
    surface = Color(0xFFF7FBFE),
    onBackground = Color(0xFF2F4A66),
    onSurface = Color(0xFF2F4A66)
)

@Composable
fun XToolCollabTheme(content: @Composable () -> Unit) {
    MaterialTheme(
        colorScheme = LightColors,
        content = content
    )
}
