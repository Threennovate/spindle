package com.spindletrack.android.ui.theme

import androidx.compose.foundation.isSystemInDarkTheme
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Typography
import androidx.compose.material3.darkColorScheme
import androidx.compose.material3.lightColorScheme
import androidx.compose.runtime.Composable
import androidx.compose.runtime.CompositionLocalProvider
import androidx.compose.runtime.Immutable
import androidx.compose.runtime.ReadOnlyComposable
import androidx.compose.runtime.staticCompositionLocalOf
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.text.TextStyle
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.sp

/**
 * Spindle's colours beside Material's, as in branding/README.md: marigold is used sparingly, for the main button,
 * the timer's start button and now in the calendar, with graphite ink on it. Text and symbols in the brand colour are
 * bronze in light mode, where marigold would wash out, and marigold in dark. Grounds are neutral, never a wash of
 * marigold.
 */
@Immutable
data class SpindleColors(
    /** Marigold, for fills such as the main button and the start button. */
    val brand: Color,
    /** Text and symbols in the brand colour. */
    val brandText: Color,
    /** Graphite, for text and symbols on marigold in both modes. */
    val ink: Color,
    /** The quiet fill of chips and small controls. */
    val chip: Color,
    /** The hairline between rows. */
    val hairline: Color,
    val danger: Color,
    /** Good news in words, e.g. a paid plan: the iPhone's green, darker in light mode so text reads. */
    val success: Color,
    val dark: Boolean,
)

private val LightSpindle = SpindleColors(
    brand = Color(0xFFF0B21B), brandText = Color(0xFF8F5D04), ink = Color(0xFF141518),
    chip = Color(0x14767680), hairline = Color(0x1F3C3C43), danger = Color(0xFFE5342B), success = Color(0xFF248A3D), dark = false,
)
private val DarkSpindle = SpindleColors(
    brand = Color(0xFFF7BE2B), brandText = Color(0xFFF7BE2B), ink = Color(0xFF141518),
    chip = Color(0x2E767680), hairline = Color(0x24FFFFFF), danger = Color(0xFFFF5F55), success = Color(0xFF30D158), dark = true,
)

// The grounds follow grouped lists: a soft grey page with white cards in light mode, graphite with raised cards in
// dark. Primary is the brand text colour, so Material's own text buttons, switches and focus read in it.
private val LightColors = lightColorScheme(
    primary = Color(0xFF8F5D04), onPrimary = Color.White,
    primaryContainer = Color(0xFFF0B21B), onPrimaryContainer = Color(0xFF141518),
    secondary = Color(0xFF5E6068), onSecondary = Color.White,
    secondaryContainer = Color(0xFFE4E4E9), onSecondaryContainer = Color(0xFF141518),
    tertiary = Color(0xFF8F5D04), onTertiary = Color.White,
    background = Color(0xFFF2F2F5), onBackground = Color(0xFF141518),
    surface = Color(0xFFF2F2F5), onSurface = Color(0xFF141518),
    surfaceVariant = Color(0xFFE4E4E9), onSurfaceVariant = Color(0xFF6C6E76),
    surfaceTint = Color.Transparent,
    surfaceContainerLowest = Color.White, surfaceContainerLow = Color.White,
    surfaceContainer = Color(0xFFF8F8FA), surfaceContainerHigh = Color.White,
    surfaceContainerHighest = Color(0xFFE4E4E9),
    surfaceBright = Color.White, surfaceDim = Color(0xFFE2E2E6),
    inverseSurface = Color(0xFF2A2B30), inverseOnSurface = Color(0xFFF2F2F5), inversePrimary = Color(0xFFF7BE2B),
    outline = Color(0xFFC4C5CB), outlineVariant = Color(0xFFDCDCE1),
    error = Color(0xFFD92B2B), onError = Color.White, errorContainer = Color(0xFFFDE7E7), onErrorContainer = Color(0xFF7A1010),
    scrim = Color.Black,
)
private val DarkColors = darkColorScheme(
    primary = Color(0xFFF7BE2B), onPrimary = Color(0xFF141518),
    primaryContainer = Color(0xFFF7BE2B), onPrimaryContainer = Color(0xFF141518),
    secondary = Color(0xFFB4B6BE), onSecondary = Color(0xFF141518),
    secondaryContainer = Color(0xFF34353B), onSecondaryContainer = Color(0xFFECEDF1),
    tertiary = Color(0xFFF7BE2B), onTertiary = Color(0xFF141518),
    background = Color(0xFF0F1012), onBackground = Color(0xFFECEDF1),
    surface = Color(0xFF0F1012), onSurface = Color(0xFFECEDF1),
    surfaceVariant = Color(0xFF2A2B30), onSurfaceVariant = Color(0xFF9A9CA5),
    surfaceTint = Color.Transparent,
    surfaceContainerLowest = Color(0xFF141518), surfaceContainerLow = Color(0xFF1C1D21),
    surfaceContainer = Color(0xFF17181B), surfaceContainerHigh = Color(0xFF1C1D21),
    surfaceContainerHighest = Color(0xFF2E2F35),
    surfaceBright = Color(0xFF2A2B30), surfaceDim = Color(0xFF0F1012),
    inverseSurface = Color(0xFFECEDF1), inverseOnSurface = Color(0xFF141518), inversePrimary = Color(0xFF8F5D04),
    outline = Color(0xFF4A4B52), outlineVariant = Color(0xFF34353B),
    error = Color(0xFFFF6B61), onError = Color(0xFF141518), errorContainer = Color(0xFF3D1715), onErrorContainer = Color(0xFFFFDAD6),
    scrim = Color.Black,
)

/** Figures that line up and don't jump as a timer counts. */
private const val TabularFigures = "tnum"

private val Base = Typography()
private val SpindleTypography = Typography(
    displayLarge = Base.displayLarge.copy(fontFeatureSettings = TabularFigures),
    displayMedium = Base.displayMedium.copy(fontFeatureSettings = TabularFigures),
    displaySmall = Base.displaySmall.copy(fontFeatureSettings = TabularFigures),
    headlineLarge = Base.headlineLarge.copy(fontWeight = FontWeight.Bold, fontSize = 32.sp, lineHeight = 38.sp),
    headlineMedium = Base.headlineMedium.copy(fontWeight = FontWeight.SemiBold),
    headlineSmall = Base.headlineSmall.copy(fontWeight = FontWeight.SemiBold, fontFeatureSettings = TabularFigures),
    titleLarge = Base.titleLarge.copy(fontWeight = FontWeight.SemiBold),
    titleMedium = Base.titleMedium.copy(fontWeight = FontWeight.SemiBold, fontFeatureSettings = TabularFigures),
    titleSmall = Base.titleSmall.copy(fontFeatureSettings = TabularFigures),
    bodyLarge = Base.bodyLarge.copy(fontFeatureSettings = TabularFigures, letterSpacing = 0.15.sp),
    bodyMedium = Base.bodyMedium.copy(fontFeatureSettings = TabularFigures),
    bodySmall = Base.bodySmall.copy(fontFeatureSettings = TabularFigures),
    labelLarge = Base.labelLarge.copy(fontFeatureSettings = TabularFigures),
    labelMedium = Base.labelMedium.copy(fontFeatureSettings = TabularFigures),
    labelSmall = Base.labelSmall.copy(fontFeatureSettings = TabularFigures),
)

private val LocalSpindleColors = staticCompositionLocalOf { LightSpindle }

object Spindle {
    val colors: SpindleColors
        @Composable @ReadOnlyComposable get() = LocalSpindleColors.current
}

/** A style with figures that line up, for durations and times. */
fun TextStyle.tabular(): TextStyle = copy(fontFeatureSettings = TabularFigures)

@Composable
fun SpindleTheme(dark: Boolean = isSystemInDarkTheme(), content: @Composable () -> Unit) {
    CompositionLocalProvider(LocalSpindleColors provides if (dark) DarkSpindle else LightSpindle) {
        MaterialTheme(colorScheme = if (dark) DarkColors else LightColors, typography = SpindleTypography, content = content)
    }
}
