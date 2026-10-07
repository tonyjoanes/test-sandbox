// Fixture: references a flag that exists in the fixture registry.
const enabled = await featureManager.isEnabled('known-flag');
console.log(enabled);
