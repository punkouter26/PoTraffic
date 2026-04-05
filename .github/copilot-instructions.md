General Engineering Principles + .NET API
Global Cleanup: Maintain a "zero-waste" codebase by deleting unused files, dead code, and obsolete assets.
Apply GoF/SOLID patterns + explanatory comments when possible
For any major feature created , create corresponding UNIT (C#), INTEGRATION(C#), E2E tests (Typescript)
Architecture: Onion Architecture for server project
When there are errors add debug info to loggers to get details related to the error and allow those details to be shown in the UI as well
When fixing a problem add detailed logs around the location of the problem to assist in debugging
Any feature that would be useful to adjust add to appSettings as a feature flag so I can change how app behaves without changing code
Only create doc files automatically if they will help the coding LLM understand the code base better and quicker
If the task is not totally clear, ASK CLARIFYING QUESTIONS UNTIL IT IS!
Blazor WASM +  .NET 10 API (Azure App Service (BLAZOR WASM + .NET core API in same App Service)
Use Blazor Radzen UI controls when advanced UI is needed
