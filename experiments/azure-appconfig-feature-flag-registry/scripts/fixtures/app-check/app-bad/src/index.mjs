// Fixture: references a flag that does NOT exist in the fixture registry — a typo or a
// flag someone forgot to add. This is exactly the bug the compile-time check exists to catch.
const enabled = await featureManager.isEnabled('typo-flag');
console.log(enabled);
