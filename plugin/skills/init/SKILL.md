---
description: Create this machine's rtfc identity (a person CA and a device certificate). Run once per device.
argument-hint: "[--handle <name>] [--device <name>] [--port <port>] [--hint-host <host>]"
allowed-tools: Bash(rtfc init:*)
disable-model-invocation: true
---
!`rtfc init $ARGUMENTS`

Tell the user the result above in a sentence or two, including the fingerprint. Do not run any other rtfc commands.
