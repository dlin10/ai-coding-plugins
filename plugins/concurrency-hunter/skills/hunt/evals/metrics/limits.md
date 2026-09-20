| dimension | depth | contexts | scc | demo ok | demo loss | eshop findings | eshop fingerprints | eshop loss | eshop seconds |
|---|---|---|---|---|---|---|---|---|---|
| depth | 4 | 16 | 64 | true | 9 | 0 | e3b0c44298fc1c14 | 0 | 33.7 |
| depth | 8 | 16 | 64 | true | 5 | 0 | e3b0c44298fc1c14 | 0 | 27.6 |
| depth | 12 | 16 | 64 | true | 1 | 0 | e3b0c44298fc1c14 | 0 | 27.5 |
| contexts | 8 | 4 | 64 | true | 0 | 85 | 1a3e4dd0d9bcdb16 | 16 | 27.2 |
| contexts | 8 | 16 | 64 | true | 0 | 0 | e3b0c44298fc1c14 | 0 | 27.6 |
| contexts | 8 | 64 | 64 | true | 0 | 0 | e3b0c44298fc1c14 | 0 | 27.3 |
| scc | 8 | 16 | 16 | true | 0 | 0 | e3b0c44298fc1c14 | 0 | 27.2 |
| scc | 8 | 16 | 64 | true | 0 | 0 | e3b0c44298fc1c14 | 0 | 27.6 |
| scc | 8 | 16 | 256 | true | 0 | 0 | e3b0c44298fc1c14 | 0 | 27.3 |

Chosen: depth=8, contexts=16, scc=16

## Published executable size

Measured after the solver joined the package in phase 4. SPEC 14.3 asks for the size to be measured, not gated, so there is no ceiling here; the number is checked against the file it describes.

| File | Bytes | Measured | Engine |
|---|---|---|---|
| concurrency-hunter.exe | 49051566 | 2026-09-19 | 0.1.0 |
| concurrency-hunter.exe | 49084334 | 2026-09-20 | 0.1.0 |
