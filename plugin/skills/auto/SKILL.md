---
description: Let a headless, read-only Claude answer one contact, or every current contact, automatically from one directory, or turn that off.
argument-hint: "<contact>|--all off|headless [--scope <dir>]"
allowed-tools: Bash(rtfc auto:*)
disable-model-invocation: true
---
!`rtfc auto $ARGUMENTS`
Tell the user the result above in one or two sentences. If it turned auto-answer on, say which directory the answering Claude can read (without --scope it is the directory this session runs in), and remind them that it can read everything under that directory except files that look like secrets, so it should not contain anything they would not show those contacts. Do not run any other rtfc commands.
