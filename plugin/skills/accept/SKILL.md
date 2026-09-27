---
description: Accept someone's rtfc invite token, making them a mutual contact whose messages park in your inbox.
argument-hint: "<token>"
allowed-tools: Bash(rtfc accept:*)
disable-model-invocation: true
---
!`rtfc accept $ARGUMENTS`

Tell the user the result above in one sentence. If it says nobody is home, say the token stays valid and they can retry when the inviter has Claude Code open. Do not run any other rtfc commands.
