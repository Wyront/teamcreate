# Team Create for S&box

Collaborative editing plugin for S&box — real-time file sync between participants via WebSocket hub.

## Installation

1. Navigate to your S&box project's `Libraries` folder
2. Clone this repository:
```bash
cd Libraries
git clone https://github.com/your-username/teamcreate.git
```
3. Restart S&box editor

## Building the Hub

The hub is a standalone WebSocket server (ASP.NET Core Kestrel) that coordinates file sync between participants.

```bash
cd Hub
dotnet publish -c Release
```

Publish settings (single-file, self-contained win-x64, trimmed) are baked into `TeamCreateHub.csproj`.
The executable will be at `Hub/publish/TeamCreateHub.exe` (~13 MB).

```
TeamCreateHub.exe [--port 4877] [--password secret] [--tunnel] [--log hub.log]
```

Steam link (no port forwarding, no public IP, no extra software beyond Steam):

```
# Host machine:
TeamCreateHub.exe --steam-host --password secret
# prints: lobby code: 109775244...

# Guest machine:
TeamCreateHub.exe --steam-join 109775244469095421 --password secret
```

The guest editor connects to `127.0.0.1:4877` as usual — hub pipes it to the host over
Steam Datagram Relay. `--steam-appid` defaults to 480 (Spacewar test ID); for shipping
a community build, get your own AppID via Steam Direct and pass `--steam-appid <id>`.

Note: always rebuild with `--no-incremental` (MSBuild fast up-to-date check is unreliable here and will silently reuse a stale binary).

## Internet relay (one VPS, zero user setup)

Direct connections die behind NAT/VPN/proxy, and Cloudflare tunnels are throttled
to 16KB/connection inside Russia — so for internet play, host the hub on a cheap
VPS (Aeza/Timeweb/Selectel, ~300 RUB/mo, any location with good peering to your ISPs):

```bash
# on the VPS (Ubuntu):
sudo apt install -y dotnet-runtime-10.0  # or copy the self-contained exe
certbot certonly --standalone -d relay.example.com
./TeamCreateHub.exe --tls-port 443 \
  --tls-cert /etc/letsencrypt/live/relay.example.com/fullchain.pem \
  --tls-key /etc/letsencrypt/live/relay.example.com/privkey.pem \
  --password secret --log hub.log
```

Then in the editor: Address = `127.0.0.1:4877` (or empty room use), **Relay = `relay.example.com:443`**,
same Room + password on both sides. The client races loopback first and falls back
to the relay automatically — one Connect button, no juggling. The local hub is not
needed when a relay is configured (but harmless to run).

## Usage

1. **Host**: Start the hub (`TeamCreateHub.exe`), then connect from the editor
2. **Participants**: Enter the hub address (IP:port), name, room, and password → Connect
3. All project files sync automatically between participants

## Features

- Real-time file synchronization
- Password-protected rooms
- Git integration (local + remote)
- Auto-commit with configurable intervals
- Multi-language support (EN/RU)
