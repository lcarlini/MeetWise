# MeetWise
An AI-powered meeting assistant that captures conversations without bots, delivers accurate transcriptions, generates intelligent summaries and actionable insights, and enhances communication with noise cancellation and real-time accent conversion.


## Status

Phase 1 (foundation) is in place. See the product and architecture plan for the full roadmap (local-first realtime audio enhancement, streaming transcription, meeting intelligence).

| Project | Purpose |
|---|---|
| `src/MeetWise.Core` | Settings, logging, hardware tier detection, model downloader (SHA-256) |
| `src/MeetWise.Audio` | Device enumeration, bot-free dual-channel recorder (mic + system loopback) |
| `src/MeetWise.Storage` | SQLite + FTS5 meetings, transcript segments, action items |
| `src/MeetWise.App` | WPF shell |
| `tests/MeetWise.Tests` | Unit tests |

Build and test (.NET 10 SDK, Windows 11):

```
dotnet build MeetWise.slnx
dotnet test tests/MeetWise.Tests
dotnet run --project src/MeetWise.App
```
