"""Build, test, package and deploy the Inventory API (a C# minimal API) with Dagger.

Every step of the pipeline is a function here. Run any of them locally exactly as CI
does, e.g. `dagger call test`, `dagger call smoke-test` or `dagger call ci`.
"""

from .main import InventoryApi as InventoryApi
