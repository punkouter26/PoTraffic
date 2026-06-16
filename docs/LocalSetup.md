# Local Setup Guide

The authoritative local onboarding guide is located in the feature specification folder:

👉 [**PoTraffic Quickstart Guide**](../specs/00001-potraffic-core/quickstart.md)

Refer to that document for the most up-to-date instructions on:
- Cloning & Bootstrapping
- Infrastructure (Docker/Azurite)
- Table Storage setup
- Running & Testing

For social login setup (Google + Microsoft), see:

👉 [**Social Auth Setup**](./SocialAuthSetup.md)

## Docker Compose

Run `docker compose up -d` to start the local Azurite Table Storage emulator.
The API and Blazor WASM client are then launched together through
`./SCRIPTS/start-dev.ps1` on fixed ports 5000/5001.
