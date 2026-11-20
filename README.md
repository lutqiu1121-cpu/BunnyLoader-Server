# BunnyLoader Server

A standalone, incremental server implementation for the Bunny Loader-modified Terraria mobile client. This repository contains independent server code and protocol observations; it does not include Terraria or Bunny Loader source code.

## Status

R5 connection and world-entry flow is regression-tested. The current R6 implementation includes an in-memory tile overlay, per-player SSC profile persistence and override, and world-file discovery. Item drops, enemies, projectiles, damage, buffs, and chests are **not implemented** because exact serialization for this modified mobile client has not been established. Do not assume vanilla TShock packet layouts apply.

## Use

Download `bunny-loader-server-panel.zip` from [GitHub Releases](https://github.com/lutqiu1121-cpu/BunnyLoader-Server/releases/tag/panel), extract it to the server root, and use the panel's .NET 8 runtime. Put the world file at `world.wld`, `world/world.wld`, or as the only `.wld` file in `world/`.

**Startup command is a compatibility contract. Do not change its spelling, port, or wrapping:**

```bash
mise use -g dotnet@8.0.408 && env WORLD_FILE=world.wld dotnet BunnyLoader.Server.dll --port 32054
```

Player profiles are stored under `Players/<player-name>.plr`. The archive includes a generated, synthetic default template. Back up `Players/` and the world file before testing. This is experimental server software, not a production Terraria server replacement.

## Build and tests

CI uses Docker to compile the .NET 8 service, run protocol/profile self-tests, and exercise the handshake/world-entry flow with a synthetic client. Local checks:

```bash
sh tools/check-startup-command.sh
docker build -t bunny-loader-server:ci ./server
docker run --rm bunny-loader-server:ci --selftest
docker run --rm bunny-loader-server:ci --plr-selftest
python3 tools/proto_ref.py selftest
```

## Protocol safety

The mobile client is modified. Preserve the tested R5 sequence (`7 -> 10×40 -> 49 -> 14 -> 12 -> 129`) and never parse id:13 coordinates. IDs whose payload layout has not been verified against this client are intentionally ignored; do not add guessed packet serializers.

## License and upstream notice

This repository contains an independent implementation and protocol observations, not Terraria or Bunny Loader source code. See [LICENSE](LICENSE). Compatibility with third-party software is not an endorsement by its authors. Users are responsible for complying with applicable game, mod, and hosting terms.
