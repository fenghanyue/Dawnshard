# Guest authentication service

This isolated service implements the Dragalia Lost guest SDK protocol using
DragaliaLostRevival/DragaliaBaasServer at commit
`91e9a5466d41e4e2d10d0701cc87adaa55d68948` as the reference implementation.
Protocol DTO fields and the analytics response are adapted from that project.
Source: https://github.com/DragaliaLostRevival/DragaliaBaasServer

This directory is distributed under GNU GPL version 3; see COPYING.
It communicates with the game API over HTTP and is a separate executable.

The persistent credential store, one-time upstream identity verification,
concurrency control, key persistence and deployment integration are maintained
in this repository. Existing device identities are enrolled only after a valid
upstream login and JWT verification. A supplied previous user ID is never proof
of ownership. An upstream failure never creates a replacement local identity.

No device passwords, ID tokens or upstream response bodies are logged.
