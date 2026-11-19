import time
from typing import Annotated

import dagger
from dagger import DefaultPath, Doc, Ignore, dag, function, object_type

SDK_IMAGE = "mcr.microsoft.com/dotnet/sdk:10.0"
RUNTIME_IMAGE = "mcr.microsoft.com/dotnet/aspnet:10.0"
AZURE_CLI_IMAGE = "mcr.microsoft.com/azure-cli:2.93.0"
CURL_IMAGE = "curlimages/curl:8.17.0"

API_PROJECT = "src/Inventory.Api/Inventory.Api.csproj"
API_PORT = 8080


@object_type
class InventoryApi:
    """Build, test, package and deploy the Inventory API: a pipeline written as code."""

    source: Annotated[
        dagger.Directory,
        Doc("The experiment folder (defaults to the folder holding dagger.json)"),
        DefaultPath("."),
        Ignore(["**/bin", "**/obj", "**/TestResults", ".dagger", "README.md"]),
    ]

    # ---------------------------------------------------------------- build + test

    @function
    def build_env(self) -> dagger.Container:
        """A .NET SDK container with the source mounted and packages restored."""
        return (
            dag.container()
            .from_(SDK_IMAGE)
            .with_env_variable("DOTNET_CLI_TELEMETRY_OPTOUT", "1")
            .with_env_variable("DOTNET_NOLOGO", "1")
            .with_env_variable("TESTINGPLATFORM_TELEMETRY_OPTOUT", "1")
            # A cache volume persists between runs, so NuGet packages download once.
            .with_mounted_cache("/root/.nuget/packages", dag.cache_volume("nuget-packages"))
            .with_directory("/src", self.source)
            .with_workdir("/src")
            .with_exec(["dotnet", "restore", "Inventory.slnx"])
        )

    @function
    async def test(self) -> str:
        """Build the solution and run the tests."""
        return await (
            self.build_env()
            .with_exec(
                ["dotnet", "test", "--solution", "Inventory.slnx", "--no-restore", "--no-progress"]
            )
            .stdout()
        )

    @function
    def publish(self) -> dagger.Directory:
        """The output of `dotnet publish` for the API."""
        return (
            self.build_env()
            .with_exec(
                ["dotnet", "publish", API_PROJECT, "-c", "Release", "--no-restore", "-o", "/out"]
            )
            .directory("/out")
        )

    # ---------------------------------------------------------------- package + run

    @function
    def container(
        self,
        version: Annotated[str, Doc("Version stamped into the image (e.g. a git SHA)")] = "dev",
    ) -> dagger.Container:
        """The runtime image: no Dockerfile, just code."""
        return (
            dag.container()
            .from_(RUNTIME_IMAGE)
            .with_directory("/app", self.publish())
            .with_workdir("/app")
            .with_env_variable("APP_VERSION", version)
            .with_env_variable("ASPNETCORE_HTTP_PORTS", str(API_PORT))
            .with_label("org.opencontainers.image.version", version)
            .with_user("app")  # the non-root user the .NET images ship with
            .with_exposed_port(API_PORT)
            .with_entrypoint(["dotnet", "Inventory.Api.dll"])
        )

    @function
    def serve(self, version: str = "dev") -> dagger.Service:
        """Run the API as a service: `dagger call serve up --ports 8080:8080`."""
        return self.container(version).as_service(use_entrypoint=True)

    @function
    async def smoke_test(self, version: str = "dev") -> str:
        """Start the real container and hit it over HTTP from a second container."""
        script = f"""
set -eu
curl -fsS http://api:{API_PORT}/health
echo
curl -fsS -X POST http://api:{API_PORT}/items \\
  -H 'Content-Type: application/json' -d '{{"name":"Widget","quantity":3}}'
echo
curl -fsS http://api:{API_PORT}/items/1
echo
curl -fsS http://api:{API_PORT}/version | grep -q '"version":"{version}"'
echo "version {version} confirmed"
"""
        return await (
            dag.container()
            .from_(CURL_IMAGE)
            # The service is started on demand and reachable by its alias, like docker-compose,
            # but scoped to this one call and torn down afterwards.
            .with_service_binding("api", self.serve(version))
            .with_exec(["sh", "-c", script])
            .stdout()
        )

    @function
    async def ci(self, version: str = "dev") -> str:
        """Everything a pull request needs: unit/integration tests, then a container smoke test."""
        tests = await self.test()
        smoke = await self.smoke_test(version)
        return f"{tests}\n--- smoke test ---\n{smoke}"

    # ---------------------------------------------------------------- deploy

    @function
    async def publish_image(
        self,
        address: Annotated[str, Doc("Full image reference, e.g. myacr.azurecr.io/inventory-api:abc123")],
        username: str,
        password: dagger.Secret,
        version: str = "dev",
    ) -> str:
        """Push the image to any registry and return the reference with its digest."""
        registry = address.split("/", 1)[0]
        return await (
            self.container(version)
            .with_registry_auth(registry, username, password)
            .publish(address)
        )

    @function
    async def deploy(
        self,
        version: Annotated[str, Doc("Image tag and APP_VERSION, normally the git SHA")],
        acr_name: Annotated[str, Doc("Azure Container Registry name (without .azurecr.io)")],
        resource_group: str,
        container_app: Annotated[str, Doc("Name of an existing Azure Container App")],
        tenant_id: str,
        client_id: Annotated[str, Doc("Service principal (app registration) client ID")],
        client_secret: Annotated[dagger.Secret, Doc("Service principal secret, e.g. env://AZURE_CLIENT_SECRET")],
    ) -> str:
        """Push the image to ACR, roll the Container App onto it, then check the live version."""
        image = await self.publish_image(
            f"{acr_name}.azurecr.io/inventory-api:{version}", client_id, client_secret, version
        )

        fqdn = await (
            dag.container()
            .from_(AZURE_CLI_IMAGE)
            .with_secret_variable("AZURE_CLIENT_SECRET", client_secret)
            # Exec results are cached by their inputs; a deploy must always run.
            .with_env_variable("CACHE_BUSTER", str(time.time_ns()))
            .with_env_variable("CLIENT_ID", client_id)
            .with_env_variable("TENANT_ID", tenant_id)
            .with_env_variable("APP", container_app)
            .with_env_variable("RG", resource_group)
            .with_env_variable("IMAGE", image)
            .with_exec(
                [
                    "sh", "-c",
                    'az login --service-principal --output none '
                    '--username "$CLIENT_ID" --password "$AZURE_CLIENT_SECRET" --tenant "$TENANT_ID" '
                    '&& az config set extension.use_dynamic_install=yes_without_prompt --only-show-errors '
                    '&& az containerapp update --name "$APP" --resource-group "$RG" --image "$IMAGE" '
                    '--query properties.configuration.ingress.fqdn --output tsv',
                ]
            )
            .stdout()
        )
        fqdn = fqdn.strip()

        live = await (
            dag.container()
            .from_(CURL_IMAGE)
            .with_env_variable("CACHE_BUSTER", str(time.time_ns()))
            .with_exec(
                ["curl", "-fsS", "--retry", "10", "--retry-delay", "6", "--retry-all-errors",
                 f"https://{fqdn}/version"]
            )
            .stdout()
        )
        if f'"version":"{version}"' not in live:
            raise ValueError(f"Deployed, but https://{fqdn}/version returned {live!r}")
        return f"Deployed {image}\nhttps://{fqdn} is serving version {version}"
