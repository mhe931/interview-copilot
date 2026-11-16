# Latency benchmark (NFR-05)

NFR-05 says the copilot must start answering within **2-5 seconds** on the reference hardware (16 GB RAM,
no GPU). This benchmark measures it through the real HTTP API and writes `docs/benchmarks/latency.md`.

It is not part of CI: CI runners are not the reference hardware. Run it by hand on the reference machine.

## What it does

1. Registers a throwaway account (`benchmark-<UTC yyyyMMddHHmmss>`, random password generated at run time),
   signs in, selects the requested provider/model on that account, creates a Live Interview session
   (Balanced length, no framework) and starts it. Cloud keys are handed to the live session in memory only.
2. Sends one warm-up question (not counted), so the model is loaded before timing starts.
3. Sends the 20 questions in `questions.json` one after another, at least two for each of the 8 question types.
4. For each question it measures, from sending the request:
   - **Prepared lookup**: backend time to look up a prepared answer, reported separately;
   - **TTFT**: time until the first `delta` event (the first answer token);
   - **Total**: time until the `done` event, including the complete answer.
   A question that errors or takes longer than 60 s is recorded as failed and left out of the statistics.
5. Ends the session and writes the report.

## Prerequisites

- [Ollama](https://ollama.com) running, with the model pulled, for example `ollama pull llama3.2:3b`.
- The backend running on the same machine: `pnpm dev:backend` (listens on `http://127.0.0.1:5480`).
- Close heavy applications first, so the numbers reflect the app and not your other work.

## Run it

From the repository root:

```powershell
dotnet run --project src\backend\tools\InterviewCopilot.Benchmark -c Release -- --provider ollama --model llama3.2:3b
```

For Google Gemini, put the key in an environment variable using your local secret-management workflow,
then pass only that variable's name (never the key itself) to the tool:

```powershell
dotnet run --project src\backend\tools\InterviewCopilot.Benchmark -c Release -- --provider google --model gemini-3.5-flash-lite --api-key-env GOOGLE_API_KEY
```

The tool reads the variable into process memory, does not print it or include it in the report, and does
not enable billing. It cannot determine whether your Google account will bill these requests; free-tier
availability, quota and billing are controlled by Google.

Options (all optional):

| Option | Default | Meaning |
|--------|---------|---------|
| `--base-url` | `http://127.0.0.1:5480` | Where the backend is listening. |
| `--questions` | `questions.json` next to the executable | A different question set (same JSON shape). |
| `--output` | `docs/benchmarks/latency.md` | Report path, relative to the current directory. Overwritten each run. |
| `--provider` | `ollama` | Provider selected on the throwaway benchmark account: `ollama`, `google`, `anthropic`, or `openai`. |
| `--model` | `llama3.2:3b` | Model selected on the throwaway benchmark account; this is not just a report label. |
| `--api-key-env` | none | Required for a cloud provider. The name of an environment variable containing the key; never put the key itself on the command line. |

Exit code: `0` once the measurement ran, **even if the verdict is FAIL**. Non-zero only when the backend cannot
be reached or sign-in fails (`Start the backend first: pnpm dev:backend`), or the arguments are invalid.

## Reading the report

- **Header**: date (UTC), provider, model, CPU, logical cores, RAM, OS. Check that it matches the reference hardware
  before committing the file.
- **Results table**: one row per question with prepared-answer lookup, TTFT and total time in milliseconds, and `ok` or `failed: <reason>`.
- **Summary**: P50 and P95 for prepared lookup, TTFT and total time, over successful runs only, using the nearest-rank method
  (the value at rank `ceil(p/100 * n)` of the sorted samples).
- **Verdict**: `PASS` when the **P95 TTFT is 5000 ms or less**. With no successful run the verdict is `FAIL`.
  Look at the number of failed questions too, because they do not count towards the percentiles.

The first run after a reboot can be slower while Ollama loads the model; the warm-up question absorbs most of that.

## Gemini model tradeoffs

Google's current documentation lists Gemini 3.5 Flash-Lite as stable at $0.30/1M input and $2.50/1M
output tokens, with minimal thinking by default. Gemini 3.8 Flash supports low, medium and high thinking
(not minimal) and has introductory prices of $0.75/1M input and $3.75/1M output through December 31,
2026; standard pricing is scheduled to begin January 1, 2027. Costs depend on prompt, answer and thinking
tokens, and access/tier eligibility must be verified for the user's own key. See Google's
[thinking guide](https://ai.google.dev/gemini-api/docs/thinking),
[Gemini 3.5/3.6 model notes](https://ai.google.dev/gemini-api/docs/whats-new-gemini-3.6),
[Gemini 3.8 model notes](https://ai.google.dev/gemini-api/docs/whats-new-gemini-3.8), and
[pricing](https://ai.google.dev/gemini-api/docs/pricing).
