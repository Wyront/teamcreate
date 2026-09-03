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

The hub is a standalone WebSocket server that coordinates file sync between participants.

```bash
cd Hub
dotnet publish -c Release -r win-x64 --self-contained
```

The executable will be at `Hub/publish/TeamCreateHub.exe`.

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
