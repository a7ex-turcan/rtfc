---
description: Approve this project's pending source subscriptions from .claude/rtfc.local.json so they start polling.
allowed-tools: Bash(rtfc sources approve:*)
disable-model-invocation: true
---
!`rtfc sources approve`
Tell the user the result above in one or two sentences: how many subscriptions were approved and that tickets now land in this project's inbox and status line within a minute or two, or what is wrong with the file if it was refused. Do not run any other rtfc commands.
