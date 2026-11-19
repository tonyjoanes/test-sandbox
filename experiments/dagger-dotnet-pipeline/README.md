# Dagger: a C# Web API Pipeline Written as Code

An experiment with [Dagger](https://dagger.io): a small ASP.NET Core minimal API (`Inventory.Api`)
whose whole pipeline (restore, build, test, package as a container, smoke test the running
container, push, deploy to Azure Container Apps) is a set of **functions in a Dagger module**,
not steps in a YAML file.

The headline: **the CI workflow is two steps** (install the CLI, call a function), and you run
exactly the same thing on your laptop:

```bash
dagger call ci                     # what a pull request runs
dagger call deploy --version=...   # what a manual deploy runs
```

---

## Where Dagger Sits (vs. Pulumi, YAML, Bicep)

These get lumped together, but they do different jobs:

| Tool | What it describes | Written in |
|---|---|---|
| **Bicep / ARM** | Azure *infrastructure* (what should exist) | Declarative DSL |
| **Pulumi** | Infrastructure, any cloud (what should exist) | C#, TS, Python, Go… |
| **Azure Pipelines / GitHub Actions YAML** | *Pipeline* steps, plus where they run | YAML + shell |
| **Dagger** | *Pipeline* steps, with no tie to where they run | Go, Python, TS, Java, PHP… |

So Dagger is closest to **"Pulumi, but for the pipeline instead of the infrastructure"**. It
replaces the logic inside your YAML (the `script:` blocks, the `docker build`, the test commands),
not your Bicep. The YAML doesn't disappear: it shrinks to a trigger that calls Dagger
(see [`.github/workflows/dagger-dotnet-pipeline.yml`](../../.github/workflows/dagger-dotnet-pipeline.yml)).
You could run Bicep or Pulumi *from* a Dagger function, just as you run them from YAML today.

### The ideas that make it different

1. **Every step runs in a container.** The pipeline asks for `mcr.microsoft.com/dotnet/sdk:10.0`
   in code, so the agent needs nothing installed except Docker (or another container runtime) and the Dagger CLI. No
   `UseDotNet@2` task, no "works on the hosted agent but not on mine".
2. **Steps are typed functions, not strings.** `publish()` returns a `Directory`, `container()`
   returns a `Container`, `serve()` returns a `Service`. You compose them like normal code:
   `container()` calls `publish()`, which calls `build_env()`. There's no `artifacts/upload` then
   `artifacts/download` between stages; you just pass the value along.
3. **Everything is cached by its inputs.** Dagger is built on BuildKit (the engine behind
   `docker build`), so every operation is a cached layer. Change nothing and `dagger call ci`
   re-runs in seconds; change one `.cs` file and only the steps downstream of it re-run. NuGet
   packages live in a named cache volume that persists between runs.
4. **Local == CI.** The engine is the same on a laptop and on a runner, so "push and pray" goes
   away. You debug the pipeline by running it.
5. **Services are first-class.** `smoke_test()` starts the real API container as a service, binds
   it to a hostname (`api`) and curls it from a second container, all inside one function call,
   torn down afterwards. That's a docker-compose integration test without the compose file.
6. **Secrets are a type.** `client_secret: dagger.Secret` can't be printed by accident, is
   scrubbed from logs and never lands in the cache. You pass it by reference
   (`env://AZURE_CLIENT_SECRET`, `file://...`, `op://...` for 1Password, `vault://...`).

---

## What's Here

```
dagger-dotnet-pipeline/
├── dagger.json                         # marks this folder as a Dagger module
├── .dagger/                            # the pipeline (Python SDK)
│   ├── pyproject.toml, uv.lock
│   └── src/inventory_api/main.py       # ← the whole pipeline, ~200 lines
├── Inventory.slnx, global.json, Directory.Build.props
├── src/Inventory.Api/                  # .NET 10 minimal API: /health, /version, /items
└── tests/Inventory.Api.Tests/          # MSTest (Microsoft.Testing.Platform) + WebApplicationFactory
```

Note what's missing: **there is no Dockerfile.** The runtime image is built in `container()`.

### The functions

```bash
$ dagger functions
build-env       A .NET SDK container with the source mounted and packages restored.
ci              Everything a pull request needs: unit/integration tests, then a container smoke test.
container       The runtime image: no Dockerfile, just code.
deploy          Push the image to ACR, roll the Container App onto it, then check the live version.
publish         The output of `dotnet publish` for the API.
publish-image   Push the image to any registry and return the reference with its digest.
serve           Run the API as a service: `dagger call serve up --ports 8080:8080`.
smoke-test      Start the real container and hit it over HTTP from a second container.
test            Build the solution and run the tests.
```

`ci` is just `test` then `smoke_test`. `deploy` is `publish_image` (to ACR) → `az containerapp
update` → curl the live `/version` and fail unless it reports the commit just deployed.

---

## Try It

You need Docker running and the [Dagger CLI](https://docs.dagger.io/install) (`brew install
dagger/tap/dagger`, or `winget install Dagger.Cli`, or the install script). This was built and
tested with **Dagger v0.21.12**. No .NET SDK is needed on your machine.

```bash
cd experiments/dagger-dotnet-pipeline

dagger functions                          # list what the module can do
dagger call test                          # build + run the MSTest suite in a container
dagger call smoke-test                    # run the real image and hit it over HTTP
dagger call ci --version=$(git rev-parse --short HEAD)

dagger call serve up --ports 8080:8080    # then: curl localhost:8080/items
dagger call container export --path=inventory.tar     # the image as a tarball (docker load -i)
dagger call publish export --path=./out               # the dotnet publish output on your disk

# Drop into a shell in any container in the pipeline (e.g. the SDK container after restore):
dagger call build-env terminal
```

Things worth trying to get a feel for it:

- **Run `dagger call ci` twice.** The second run is almost instant (see the results below).
- **Break a test** and run `dagger call test`: it fails with the test output, exactly as CI would.
- **Run `dagger` with no arguments** for an interactive shell where you can chain functions:
  `container | with-exec ls /app | stdout`.
- **Open the trace.** `dagger call ci` prints a URL for a free Dagger Cloud trace view (optional;
  set `DAGGER_NO_NAG=1` to hide the hint).

### What actually ran (verified in this sandbox, Dagger v0.21.12)

| Command | Result |
|---|---|
| `dagger call ci --version=local-test` (cold) | ✅ 6/6 tests passed, smoke test passed, ~55s |
| `dagger call ci --version=local-test` (warm) | ✅ ~5s, everything from cache |
| `dagger call serve up --ports 8080:8080` | ✅ `curl localhost:8080/version` → `{"version":"from-serve"}`, validation 400s work |
| `dagger call publish-image --address=<local registry>/inventory-api:abc123 ...` | ✅ pushed, returned `...:abc123@sha256:c00b...` |
| `dagger call deploy ...` | ⚠️ **Not run.** No Azure subscription here. The ACR push is the same code path as the line above; `az containerapp update` and the live check are untested |

---

## Deploying to Azure Container Apps

`deploy` updates an **existing** Container App, so create the Azure resources once. That's
Bicep's or Pulumi's job in real life; here are the CLI equivalents:

```bash
RG=rg-dagger-inventory; LOC=uksouth; ACR=<globally-unique-acr-name>; APP=inventory-api

az group create -n $RG -l $LOC
az acr create -n $ACR -g $RG --sku Basic
az containerapp env create -n cae-dagger-inventory -g $RG -l $LOC
az containerapp create -n $APP -g $RG --environment cae-dagger-inventory \
  --image mcr.microsoft.com/k8se/quickstart:latest \
  --ingress external --target-port 8080 \
  --registry-server $ACR.azurecr.io --registry-identity system   # app pulls from ACR with its managed identity

# The identity the pipeline deploys as: Contributor on this resource group covers ACR push
# and Container App updates.
az ad sp create-for-rbac --name sp-dagger-inventory-deployer \
  --role Contributor --scopes $(az group show -n $RG --query id -o tsv)
```

The placeholder image listens on port 80, so the app is unhealthy until the first deploy
replaces it. Then deploy from your machine:

```bash
export AZURE_CLIENT_SECRET=<password from create-for-rbac>
dagger call deploy --version=$(git rev-parse --short HEAD) \
  --acr-name=$ACR --resource-group=$RG --container-app=$APP \
  --tenant-id=<tenant> --client-id=<appId> --client-secret=env://AZURE_CLIENT_SECRET
```

Or from GitHub: add repository **variables** `ACR_NAME`, `AZURE_RESOURCE_GROUP`,
`CONTAINER_APP_NAME`, `AZURE_TENANT_ID`, `AZURE_CLIENT_ID`, a secret `AZURE_CLIENT_SECRET`
(on an `azure` environment), then run the workflow manually (**Actions → Dagger .NET Pipeline →
Run workflow**). Pull requests and pushes only run `ci`.

### Running it from Azure DevOps instead

Nothing in the module is tied to GitHub. The Azure Pipelines equivalent is the same two steps:

```yaml
steps:
  - script: curl -fsSL https://dl.dagger.io/dagger/install.sh | BIN_DIR=$HOME/.local/bin sh
    env: { DAGGER_VERSION: 0.21.12 }
  - script: dagger call ci --version=$(Build.SourceVersion)
    workingDirectory: experiments/dagger-dotnet-pipeline
```

Moving CI providers stops being a rewrite: you change the trigger file and nothing else.

### What about a Function App?

The same shape works. `publish()` stays the same apart from the project (a Functions isolated-worker
project); then either:
- **containerised:** keep `container()` with `mcr.microsoft.com/azure-functions/dotnet-isolated:4-dotnet-isolated10.0`
  as the base and deploy to Functions on Container Apps; or
- **zip deploy:** mount `publish()` into the `azure-cli` image, zip it, and run
  `az functionapp deployment source config-zip`, the same way `deploy` runs
  `az containerapp update` here.

---

## Honest Observations

- **There's no C# SDK for Dagger modules (yet).** v0.21.12 rejects `--sdk=dotnet`. The pipeline
  is in Python, which reads like pseudo-code; Go and TypeScript also work. For a .NET team that's
  the biggest adoption question: the pipeline is in a second language. (The *app* can be
  anything, of course.)
- **You need a container runtime on the agent.** GitHub's and Azure DevOps' hosted Linux agents
  have Docker. Windows-hosted agents and locked-down self-hosted agents may not.
- **Cold runs are slower than a hosted agent with the SDK pre-installed**, because images are
  pulled and the engine starts. Warm runs are much faster. On ephemeral CI runners the cache
  starts empty each time unless you keep the engine warm (a persistent runner, or Dagger Cloud's
  shared cache).
- **Deploy uses a client secret for simplicity.** In practice use workload identity federation
  (GitHub OIDC → `az login --federated-token`) so no long-lived secret exists. That plugs into
  the same `dagger.Secret` parameter.
- **Exec results are cached, which is wrong for deploys.** `deploy` adds a timestamp env var
  (`CACHE_BUSTER`) so the `az` and live-check steps always run. It's the standard Dagger pattern,
  but an easy one to forget.
- **Behind a TLS-intercepting proxy** (as in the sandbox this was built in), the engine needs the
  proxy's CA mounted at `/usr/local/share/ca-certificates/` and `HTTPS_PROXY` set on the engine
  container. See Dagger's "custom runner" docs. That's corporate-network homework to expect.
