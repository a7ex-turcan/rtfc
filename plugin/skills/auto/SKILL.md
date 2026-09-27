---
description: Let a headless, read-only Claude answer one contact automatically from one directory, or turn that off.
argument-hint: "<contact> off|headless [--scope <dir>]"
allowed-tools: Bash(rtfc auto:*)
disable-model-invocation: true
---
!`rtfc auto $ARGUMENTS`

Tell the user the result above in one or two sentences. If it turned auto-answer on, remind them that the answering Claude can read everything under the scope directory except files that look like secrets, so the scope should not contain anything they would not show that contact. Do not run any other rtfc commands.
