---
description: Keep the rtfc daemon running all day, starting at login, or go back to running it only while Claude Code is open.
argument-hint: "[on|off]"
allowed-tools: Bash(rtfc daemon always-on:*)
disable-model-invocation: true
---
!`rtfc daemon always-on $ARGUMENTS`
Tell the user the result above in one or two sentences. If it turned always-on on, say that contacts now see them home whenever they are logged in and their messages wait in the inbox, that sources are polled and replies delivered with Claude Code closed, and that `rtfc away on` stops listening for a while. Do not run any other rtfc commands.
