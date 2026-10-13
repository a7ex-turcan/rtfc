---
description: Show waiting rtfc messages in Claude Code's status line, by adding the rtfc status line to your user settings unless you already have one.
argument-hint: "[--replace]"
allowed-tools: Bash(rtfc statusline install:*)
disable-model-invocation: true
---
!`rtfc statusline install $ARGUMENTS`
Tell the user the result above in one or two sentences. If it says a status line is already set, give them the two choices it names: have their own status line script run `rtfc statusline` and append its output, or run `/rtfc:statusline --replace` to show rtfc's instead. Do not edit any settings file yourself and do not run any other rtfc commands.
