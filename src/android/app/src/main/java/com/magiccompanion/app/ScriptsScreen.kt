package com.magiccompanion.app

import androidx.compose.foundation.background
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import org.json.JSONObject

@Composable
internal fun ScriptsScreen(state: UiState, client: AgentClient) {
    LaunchedEffect(Unit) { client.refresh() }
    Column(Modifier.fillMaxSize().verticalScroll(rememberScrollState())) {
        if ("tasks.read" !in state.permissions)
            Panel("快捷脚本") {
                Text("在电脑「手机与权限」授予脚本权限", fontSize = 12.sp)
            }
        else TaskCard(state, client)
    }
}

@Composable
private fun TaskCard(state: UiState, client: AgentClient) {
    var template by remember { mutableStateOf<JSONObject?>(null) }
    var parameters by remember { mutableStateOf("{}") }
    Panel("快捷脚本") {
        if (state.templates.isEmpty())
            Text("在电脑「脚本」中添加 .ps1 文件", fontSize = 12.sp, color = MaterialTheme.colorScheme.onSurfaceVariant)
        state.templates.forEach { task ->
            Row(
                Modifier.fillMaxWidth().heightIn(min = 36.dp),
                verticalAlignment = Alignment.CenterVertically,
            ) {
                Text(task.getString("name"), Modifier.weight(1f), fontSize = 13.sp, maxLines = 2, overflow = TextOverflow.Ellipsis)
                TextButton(
                    onClick = {
                        if ((task.optJSONArray("parameters")?.length() ?: 0) == 0)
                            client.runTask(task.getString("id"), JSONObject())
                        else { template = task; parameters = "{}" }
                    },
                    enabled = state.online && !state.paused && !state.busy && "tasks.run" in state.permissions,
                    modifier = Modifier.height(36.dp),
                    contentPadding = PaddingValues(horizontal = 8.dp),
                ) { Text("运行", fontSize = 12.sp) }
            }
        }
        if (state.jobs.isNotEmpty())
            Text("执行记录", fontSize = 11.sp, color = MaterialTheme.colorScheme.onSurfaceVariant)
        state.jobs.forEach { job ->
            key(job.getString("jobId")) {
                var showLogs by remember { mutableStateOf(false) }
                val status = when (job.optString("state")) {
                    "running" -> "运行中"
                    "succeeded" -> "已完成"
                    "failed" -> "执行失败"
                    "cancelled" -> "已取消"
                    else -> "状态待确认"
                }
                HorizontalDivider()
                Row(Modifier.fillMaxWidth(), verticalAlignment = Alignment.CenterVertically) {
                    Text(state.templates.firstOrNull { it.optString("id") == job.optString("templateId") }?.optString("name") ?: job.optString("templateId"), Modifier.weight(1f), fontSize = 12.sp, maxLines = 1, overflow = TextOverflow.Ellipsis)
                    Text(status, fontSize = 11.sp, color = MaterialTheme.colorScheme.primary)
                }
                Row {
                    TextButton(
                        onClick = {
                            client.refreshJob(job.getString("jobId"))
                            showLogs = !showLogs
                        },
                        enabled = state.online,
                    ) {
                        Text(if (showLogs) "收起日志" else "查看日志", fontSize = 12.sp)
                    }
                    if (job.optString("state") == "running")
                        TextButton(
                            onClick = { client.cancelJob(job.getString("jobId")) },
                            enabled = state.online,
                        ) {
                            Text("取消")
                        }
                }
                if (showLogs)
                    Text(
                        job.optJSONArray("lines")?.strings()?.takeLast(8)?.joinToString("\n").orEmpty(),
                        modifier = Modifier.fillMaxWidth().heightIn(max = 100.dp)
                            .background(MaterialTheme.colorScheme.background, RoundedCornerShape(8.dp))
                            .verticalScroll(rememberScrollState()).padding(8.dp),
                        fontSize = 11.sp,
                        color = MaterialTheme.colorScheme.onSurfaceVariant,
                    )
            }
        }
    }
    template?.let { task ->
        AlertDialog(
            onDismissRequest = { template = null },
            title = { Text("运行 ${task.getString("name")}") },
            text = {
                Column {
                    Text("只允许电脑配置的参数。无参数时保持 {}。")
                    OutlinedTextField(
                        parameters,
                        onValueChange = { parameters = it },
                        label = { Text("参数 JSON") },
                    )
                }
            },
            confirmButton = {
                TextButton(
                    onClick = {
                        runCatching { JSONObject(parameters) }
                            .onSuccess {
                                client.runTask(task.getString("id"), it)
                                template = null
                            }
                            .onFailure { client.report("参数需为 JSON 对象") }
                    }
                ) {
                    Text("运行")
                }
            },
            dismissButton = { TextButton(onClick = { template = null }) { Text("取消") } },
        )
    }
}
