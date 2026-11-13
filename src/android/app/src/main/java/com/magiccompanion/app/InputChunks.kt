package com.magiccompanion.app

internal fun committedChunks(text: String, limit: Int = 1024): List<String> {
    require(limit >= 2)
    val chunks = mutableListOf<String>()
    var start = 0
    while (start < text.length) {
        var end = (start + limit).coerceAtMost(text.length)
        if (
            end < text.length &&
                Character.isHighSurrogate(text[end - 1]) &&
                Character.isLowSurrogate(text[end])
        )
            end--
        chunks += text.substring(start, end)
        start = end
    }
    return chunks
}
