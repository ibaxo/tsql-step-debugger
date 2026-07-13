using Xunit;

namespace TsqlDbg.Integration;

// P15 and P15Ext deploy the SAME corpus file (p15_scope_identity_rowcount_chains.sql,
// shared after the M7 D1 extension). xUnit runs test CLASSES in parallel by default, so
// two concurrent CREATE OR ALTER passes over the same procs race and surface
// "definition of object ... has changed since it was compiled" plan-cache errors on the
// live server. Placing both classes in one collection serializes them (every other
// fixture deploys distinct procs and is unaffected).
[CollectionDefinition("P15SharedFixture")]
public sealed class P15SharedFixtureCollection { }
