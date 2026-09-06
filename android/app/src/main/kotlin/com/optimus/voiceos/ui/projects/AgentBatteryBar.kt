package com.optimus.voiceos.ui.projects

import androidx.compose.foundation.gestures.detectTapGestures
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.Card
import androidx.compose.material3.CardDefaults
import androidx.compose.material3.LinearProgressIndicator
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.input.pointer.pointerInput
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.optimus.voiceos.ui.theme.OptimusTokens
import kotlinx.coroutines.delay

/** A compact five-hour execution-window bar. Long-press the bar to reveal its percentage. */
@Composable
fun AgentBatteryBar(
    agentName: String,
    capacityPercent: Double,
    modifier: Modifier = Modifier
) {
    val percent = capacityPercent.coerceIn(0.0, 100.0)
    var showPercentage by remember { mutableStateOf(false) }
    LaunchedEffect(showPercentage) {
        if (showPercentage) {
            delay(2000)
            showPercentage = false
        }
    }

    Row(
        modifier = modifier
            .fillMaxWidth()
            .pointerInput(agentName, percent) {
                detectTapGestures(onLongPress = { showPercentage = true })
            },
        horizontalArrangement = Arrangement.spacedBy(8.dp),
        verticalAlignment = Alignment.CenterVertically
    ) {
        Text(agentName, color = OptimusTokens.TextSecondary, fontSize = 11.sp, modifier = Modifier.weight(1f))
        LinearProgressIndicator(
            progress = { (percent / 100.0).toFloat() },
            modifier = Modifier
                .weight(2f)
                .height(8.dp),
            color = capacityColor(percent),
            trackColor = OptimusTokens.SurfaceRaised
        )
        if (showPercentage) {
            Text("${percent.toInt()}%", color = OptimusTokens.TextPrimary, fontSize = 11.sp)
        }
    }
}

/** Capacity section used by the Projects destination. Values are replaced by live quota data later. */
@Composable
fun AgentCapacityCard(modifier: Modifier = Modifier) {
    val agents = listOf(
        "Codex / Luna" to 100.0,
        "Antigravity / Gemini" to 100.0,
        "Opus orchestrator" to 100.0
    )
    Card(
        modifier = modifier.fillMaxWidth(),
        shape = OptimusTokens.CornerCard,
        colors = CardDefaults.cardColors(containerColor = OptimusTokens.Surface)
    ) {
        Column(
            modifier = Modifier.padding(14.dp),
            verticalArrangement = Arrangement.spacedBy(9.dp)
        ) {
            Text("AGENT CAPACITY", color = OptimusTokens.TextSecondary, fontSize = 10.sp)
            agents.forEach { (name, percent) -> AgentBatteryBar(name, percent) }
        }
    }
}

private fun capacityColor(percent: Double): Color = when {
    percent < 20 -> OptimusTokens.Error
    percent <= 50 -> OptimusTokens.Warning
    else -> OptimusTokens.Accent
}
