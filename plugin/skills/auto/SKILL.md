---
description: Let Claude answer one contact, or every current contact, automatically - a headless read-only Claude from one directory, or this very session - or turn that off.
argument-hint: "<contact>|--all off|headless [--scope <dir>]|session"
allowed-tools: Bash(rtfc auto:*)
disable-model-invocation: true
---
!`rtfc auto $ARGUMENTS`
Tell the user the result above in one or two sentences. If it turned headless auto-answer on, say which directory the answering Claude can read (without --scope it is the directory this session runs in), and remind them that it can read everything under that directory except files that look like secrets, so it should not contain anything they would not show those contacts. If it turned session mode on, say that their messages now come into this session, where Claude will give the gist and ask Accept or Decline before anything runs, and that the session must have been started with --dangerously-load-development-channels plugin:rtfc@rtfc. Do not run any other rtfc commands.
