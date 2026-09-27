---
description: Show your rtfc contacts and whether they are home (Claude Code open on one of their devices).
disable-model-invocation: true
---
Call the rtfc `contacts` tool and show the user one line per contact: handle, whether each device is home, and the inbound mode. If there are no contacts, tell the user they can run `/rtfc:invite` to invite someone or `/rtfc:accept <token>` to accept an invite.
