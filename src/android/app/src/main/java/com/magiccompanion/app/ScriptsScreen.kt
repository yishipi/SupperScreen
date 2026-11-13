package com.magiccompanion.app

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
            Text("在电脑「脚本」中添加 .ps1 或 .cmd 文件", fontSize = 12.sp, color = MaterialTheme.colorScheme.onSurfaceVariant)
        state.templates.forEach { task ->
            val id = task.getString("id")
            key(id) {
                val canRun = state.online && "tasks.run" in state.permissions &&
                    (!state.paused || state.locked && task.optBoolean("allowWhileLocked"))
                ScriptItem(task, state.jobs[id], canRun, state, client) {
                    if ((task.optJSONArray("parameters")?.length() ?: 0) == 0)
                        client.runTask(id, JSONObject())
                    else {
                        template = task
                        parameters = "{}"
                    }
                }
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

@Composable
private fun ScriptItem(
    task: JSONObject,
    job: JSONObject?,
    canRun: Boolean,
    state: UiState,
    client: AgentClient,
    run: () -> Unit,
) {
    var expanded by remember { mutableStateOf(false) }
    val running = job?.optString("state") == "running"
    val status = when (job?.optString("state")) {
        null -> if (state.locked && !canRun) "解锁后可运行" else ""
        "running" -> "运行中"
        "succeeded" -> "已完成"
        "failed" -> "失败"
        "cancelled" -> "已取消"
        else -> "结果未知"
    }
    val jobId = job?.getString("jobId")
    Surface(
        modifier = Modifier.fillMaxWidth(),
        shape = RoundedCornerShape(8.dp),
        color = MaterialTheme.colorScheme.background,
    ) {
        Column(Modifier.padding(horizontal = 8.dp)) {
            Row(
                Modifier.fillMaxWidth().heightIn(min = 36.dp),
                verticalAlignment = Alignment.CenterVertically,
            ) {
                Text(
                    task.getString("name"), Modifier.weight(1f), fontSize = 12.sp,
                    maxLines = 1, overflow = TextOverflow.Ellipsis,
                )
                if (status.isNotEmpty())
                    Text(status, fontSize = 10.sp, color = MaterialTheme.colorScheme.onSurfaceVariant)
                if (jobId != null)
                    ScriptButton(if (expanded) "收起" else "日志", true) {
                        expanded = !expanded
                        if (expanded && state.online) client.refreshJob(jobId)
                    }
                if (running && jobId != null)
                    ScriptButton("取消", canRun && !state.busy) { client.cancelJob(jobId) }
                else ScriptButton("运行", canRun && !state.busy, run)
            }
            if (expanded && job != null) {
                val lines = job.optJSONArray("lines")?.strings().orEmpty()
                val code = if (job.isNull("exitCode")) null else job.optInt("exitCode")
                if (code != null)
                    Text("退出码 $code", fontSize = 10.sp, color = MaterialTheme.colorScheme.onSurfaceVariant)
                Text(
                    lines.joinToString("\n").ifBlank { if (running) "暂无输出" else "无日志" },
                    modifier = Modifier.fillMaxWidth().heightIn(max = 84.dp)
                        .verticalScroll(rememberScrollState()).padding(bottom = 5.dp),
                    fontSize = 10.sp,
                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                )
            }
        }
    }
}

@Composable
private fun ScriptButton(label: String, enabled: Boolean, onClick: () -> Unit) {
    TextButton(
        onClick = onClick,
        enabled = enabled,
        modifier = Modifier.height(36.dp),
        contentPadding = PaddingValues(horizontal = 5.dp),
    ) { Text(label, fontSize = 11.sp, maxLines = 1) }
}
