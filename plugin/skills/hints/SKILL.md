---
description: Show or change the addresses your rtfc invites tell people to connect to; add a VPN or Tailscale address for contacts outside the office.
argument-hint: "[add <host>...|remove <host>...|auto]"
allowed-tools: Bash(rtfc hints:*)
disable-model-invocation: true
---
!`rtfc hints $ARGUMENTS`
Show the user the result above. If the hints changed, say that contacts learn the new ones the next time you talk to them and that new invites carry them. Do not run any other rtfc commands.
