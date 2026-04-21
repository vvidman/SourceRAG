# SourceRAG — Testing Guide (teljesen lokális, API kulcs nélkül)

## Előfeltételek

```bash
dotnet --version    # 9.0+ vagy 10.0
docker --version
git --version
```

Szükséges még:
- **Két GGUF modell** — egy embedding-hez, egy chat-hez (ld. lent)
- Egy **GitHub repo** amit indexelni szeretnél (klónozva lokálisan)

---

## 1. lépés — Repo klónozása

```bash
git clone https://github.com/vvidman/MyProject.git ~/repos/MyProject
```

Privát repo esetén PAT-tal:
```bash
git clone https://<PAT>@github.com/vvidman/MyProject.git ~/repos/MyProject
export SOURCERAG_GIT_PAT=ghp_xxxxxxxxxxxx
```

---

## 2. lépés — Qdrant indítása

```bash
docker run -d \
  --name qdrant \
  -p 6333:6333 \
  -p 6334:6334 \
  qdrant/qdrant:latest
```

```ps
docker run -d --name qdrant -p 6333:6333 -p 6334:6334 qdrant/qdrant:latest
```


Ellenőrzés: `http://localhost:6333/dashboard` — ha betölt, rendben.

---

## 3. lépés — GGUF modellek

Két különböző modell kell: egy az embedding-hez, egy a chat completion-hoz.

### Embedding modell
Az embedding modell neve általában tartalmazza az `embed` szót.
Ajánlott: `nomic-embed-text-v1.5.Q4_K_M.gguf` (~270 MB)

```bash
mkdir -p ~/models
# Ha még nincs meg:
curl -L \
  "https://huggingface.co/nomic-ai/nomic-embed-text-v1.5-GGUF/resolve/main/nomic-embed-text-v1.5.Q4_K_M.gguf" \
  -o ~/models/nomic-embed-text.gguf
```

### Chat/LLM modell
Instruction-tuned modell kell — a neve általában tartalmaz `instruct`, `chat`, vagy `it` szót.
Ajánlott (kis méret, gyors CPU-n): `Llama-3.2-3B-Instruct-Q4_K_M.gguf` (~2 GB)

```bash
# Ha még nincs chat modelled:
curl -L \
  "https://huggingface.co/bartowski/Llama-3.2-3B-Instruct-GGUF/resolve/main/Llama-3.2-3B-Instruct-Q4_K_M.gguf" \
  -o ~/models/llama-3.2-3b-instruct.gguf
```

> **Ha már van GGUF modelled:** használd azt az `LlmModelPath`-ban. Bármely instruction-tuned modell működik — a prompt template automatikusan felismeri a SourceRAG.

> **Fontos:** ne használd ugyanazt a fájlt mindkét helyen. Az embedding modellek nem alkalmasak chat completion-re és fordítva.

---

## 4. lépés — appsettings.json konfigurálása

**`src/SourceRAG.Api/appsettings.json`:**

```json
{
  "SourceRAG": {
    "VcsProvider": "Git",
    "EmbeddingProvider": "Local",
    "LlmProvider": "Local",
    "RepositoryPath": "/home/vvidman/repos/MyProject",
    "Branch": "main",
    "Qdrant": {
      "Endpoint": "http://localhost:6333",
      "CollectionName": "sourcerag"
    },
    "LlamaSharp": {
      "ModelPath":    "/home/vvidman/models/nomic-embed-text.gguf",
      "LlmModelPath": "/home/vvidman/models/llama-3.2-3b-instruct.gguf"
    },
    "Anthropic": {
      "Model": "claude-3-5-haiku-20241022"
    },
    "OpenAiCompatible": {
      "BaseUrl": "",
      "Model": ""
    }
  },
  "AzureAd": {
    "Instance": "https://login.microsoftonline.com/",
    "TenantId": "",
    "ClientId": "",
    "Audience": ""
  },
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "Microsoft.AspNetCore": "Warning"
    }
  },
  "AllowedHosts": "*"
}
```

> **Windows path példa:**
> ```json
> "RepositoryPath": "C:\\repos\\MyProject",
> "ModelPath":    "C:\\models\\nomic-embed-text.gguf",
> "LlmModelPath": "C:\\models\\llama-3.2-3b-instruct.gguf"
> ```

> **Branch:** ellenőrizd a klónozott repóban: `git branch` — ha `master`, írd át `"master"`-re.

Nincs szükség API kulcsra — sem `ANTHROPIC_API_KEY`, sem `SOURCERAG_LLM_API_KEY` env változóra.

---

## 5. lépés — SourceRAG.Api indítása

```bash
cd /path/to/SourceRAG
dotnet run --project src/SourceRAG.Api
```

Az első indításkor az **embedding modell betöltődik** (~15–30 mp CPU-n). Várt log output:

```
info: Loading LlamaSharp model from .../nomic-embed-text.gguf
info: LlamaSharp model loaded. Embedding size: 768
info: Now listening on: https://localhost:7001
```

A chat LLM modell **lazy** töltődik — csak az első `/chat` kéréskor inicializálódik, nem indításkor.

Ha a HTTPS self-signed cert gondot okoz curl-lel, add hozzá a `src/SourceRAG.Api/appsettings.Development.json`-hoz:

```json
{
  "Kestrel": {
    "Endpoints": {
      "Http": { "Url": "http://localhost:5000" }
    }
  }
}
```

Ezután az API HTTP-n is elérhető az 5000-es porton.

---

## 6. lépés — Indexelés indítása

```bash
curl -X POST http://localhost:5000/index \
  -H "Content-Type: application/json" \
  -d '{"fullReindex": true}'
```

```ps
Invoke-WebRequest -Uri "http://localhost:5000/index" `
  -Method POST `
  -ContentType "application/json" `
  -Body '{"fullReindex": true}'
```

Az indexelés sebessége CPU-n kb. 20–50 fájl/perc (embedding modell sebességétől függ).
Közepes repó (100–300 fájl): 5–15 perc.

Státusz lekérdezése közben egy másik terminalban:
```bash
# Ismételgesd amíg az indexelés fut:
curl http://localhost:5000/index/status
```

Befejezés után várt válasz:
```json
{
  "processedFiles": 142,
  "upsertedChunks": 891,
  "deletedChunks": 0,
  "toRevision": "a3f9c12e...",
  "duration": "00:08:14"
}
```

---

## 7. lépés — Chat tesztelése

```bash
curl -X POST http://localhost:5000/chat \
  -H "Content-Type: application/json" \
  -d '{"query": "What does this project do?", "topK": 3}'
```

Az **első chat kérés lassabb** (30 mp – 3 perc CPU-n) — a LLM modell betöltődik és a first token generálódik. Utána gyorsabb.

Jó tesztkérdések:
```bash
# Architektúra
'{"query": "How is the project structured?", "topK": 5}'

# Konkrét funkció
'{"query": "How does error handling work?", "topK": 3}'

# Szerzőség
'{"query": "Who wrote the authentication module?", "topK": 3}'
```

---

## 8. lépés — Blazor Web UI

```bash
dotnet run --project src/SourceRAG.Web
```

Nyisd meg: `https://localhost:7003`

Dev módban nincs bejelentkezés — közvetlenül a Chat oldal tölt be. Az első válasz lassabb (LLM init), a többiek gyorsabbak.

A `ChunkProofCard`-okon ellenőrizd, hogy megjelenik:
- fájl neve és sorszámok
- szerző neve
- commit üzenet első sora
- revision hash (8 karakter)
- hasonlósági score (pl. `87%`)

---

## Teljesítmény elvárások CPU-n (GPU nélkül)

| | 3B modell | 7B modell |
|---|---|---|
| LLM betöltés | ~15 mp | ~40 mp |
| Chat válasz generálás | ~30–90 mp | ~3–8 perc |
| Embedding (per chunk) | ~30–80 ms | n/a |
| 100 fájl indexelés | ~3–8 perc | n/a |

GPU esetén minden 10–30x gyorsabb — LlamaSharp automatikusan használja ha van CUDA kártya.

---

## Hibaelhárítás

**"LlamaSharp:LlmModelPath is required when LlmProvider is 'Local'"**
Az `appsettings.json`-ban az `LlmModelPath` üres. Add meg a chat modell teljes path-ját.

**"No tokenizer.chat_template in GGUF metadata" — warning a logban**
Csak figyelmeztetés, nem hiba. ChatML fallback működik a legtöbb modellnél. Ha a válaszok furcsák, próbálj Llama 3, Mistral v0.3+, vagy Phi-3 modellt.

**"ANTHROPIC_API_KEY is not set" — startup hiba**
Az `appsettings.json`-ban `EmbeddingProvider` vagy `LlmProvider` értéke nem `"Local"`. Ellenőrizd mindkettőt.

**Nagyon lassú chat válasz (>5 perc)**
3B modell CPU-n 1–3 perc/válasz normális. 7B+ esetén próbálj kisebb modellt (1B–3B), vagy csökkentsd a `topK` értékét 3-ra.

**Indexelés közben "Object reference not set" vagy blame hiba**
Olyan fájlra futott, amelyhez nincs git history. Megkerülő megoldás: add hozzá a fájlt a `.gitignore`-hoz, majd futtass teljes reindexelést.

**Qdrant "collection does not exist" hiba chat-nél**
Az indexelés még nem futott le sikeresen. Futtasd újra: `curl -X POST http://localhost:5000/index -d '{"fullReindex":true}'`

---

## Gyors referencia

```bash
# 1. Qdrant
docker run -d -p 6333:6333 qdrant/qdrant

# 2. API indítása
dotnet run --project src/SourceRAG.Api

# 3. Indexelés
curl -X POST http://localhost:5000/index \
  -H "Content-Type: application/json" \
  -d '{"fullReindex": true}'

# 4. Státusz
curl http://localhost:5000/index/status

# 5. Chat teszt
curl -X POST http://localhost:5000/chat \
  -H "Content-Type: application/json" \
  -d '{"query": "How does this work?", "topK": 3}'

# 6. Web UI
dotnet run --project src/SourceRAG.Web
# → https://localhost:7003
```
