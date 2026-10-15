// The orders squad's whole application. Notice what isn't here: no connection strings, no
// keys, no endpoint configuration. The platform put endpoints in app settings (KEY_VAULT_URI
// etc.) and gave this app's managed identity the roles to use them; the manifest decided which
// of them exist.

const { app } = require('@azure/functions');
const { DefaultAzureCredential } = require('@azure/identity');
const { SecretClient } = require('@azure/keyvault-secrets');
const { AppConfigurationClient } = require('@azure/app-configuration');
const { BlobServiceClient } = require('@azure/storage-blob');
const { ServiceBusClient } = require('@azure/service-bus');

const credential = new DefaultAzureCredential();
const env = process.env;

// GET /orders/health: proves every wired capability is reachable with the app's identity,
// over private endpoints. The first demo ends with this returning all "ok".
app.http('health', {
  methods: ['GET'],
  authLevel: 'anonymous',
  route: 'health',
  handler: async () => {
    const checks = {};
    const check = async (name, fn) => {
      try {
        await fn();
        checks[name] = 'ok';
      } catch (err) {
        checks[name] = `failed: ${err.code ?? err.message}`;
      }
    };

    if (env.KEY_VAULT_URI) {
      await check('keyVault', () => new SecretClient(env.KEY_VAULT_URI, credential).listPropertiesOfSecrets().next());
    }
    if (env.APP_CONFIG_ENDPOINT) {
      await check('appConfig', () => new AppConfigurationClient(env.APP_CONFIG_ENDPOINT, credential).listConfigurationSettings().next());
    }
    if (env.DATA_BLOB_ENDPOINT) {
      await check('storage', () => new BlobServiceClient(env.DATA_BLOB_ENDPOINT, credential).getContainerClient('invoices').listBlobsFlat().next());
    }

    return { jsonBody: { squad: env.SQUAD, checks } };
  },
});

// POST /orders/place: writes an invoice blob and publishes order-placed. The payments squad's
// subscription (declared in *their* manifest) receives it.
app.http('placeOrder', {
  methods: ['POST'],
  authLevel: 'anonymous',
  route: 'place',
  handler: async (request) => {
    const order = { id: crypto.randomUUID(), placedAt: new Date().toISOString(), ...(await request.json().catch(() => ({}))) };

    const invoices = new BlobServiceClient(env.DATA_BLOB_ENDPOINT, credential).getContainerClient('invoices');
    const body = JSON.stringify(order);
    await invoices.getBlockBlobClient(`${order.id}.json`).upload(body, Buffer.byteLength(body));

    const bus = new ServiceBusClient(env.SERVICE_BUS__fullyQualifiedNamespace, credential);
    try {
      await bus.createSender(`${env.SQUAD}.order-placed`).sendMessages({ body: order, contentType: 'application/json' });
    } finally {
      await bus.close();
    }

    return { status: 201, jsonBody: order };
  },
});
