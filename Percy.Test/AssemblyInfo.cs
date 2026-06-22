using Xunit;

// Force xunit to run every test serially across the WHOLE assembly.
//
// The SDK keeps process-wide mutable static state (Percy._http, Percy._dom,
// Percy._enabled, sessionType, cliConfig, the env-flag mirrors). The driver-flow
// and logic tests mutate that state and rely on it staying put across several
// driver/HTTP calls within a single test (e.g. GetPercyDOM caching _dom, then the
// CORS-iframe block reading it). If two tests run concurrently they race on those
// statics — e.g. one test resets Percy._http to a bare HttpClient (real network)
// while another is mid-Snapshot, so its GetPercyDOM hits the live `percy
// --testing` server, gets an empty dom.js body, caches _dom = "" and silently
// skips iframe processing.
//
// xunit.runner.json already sets parallelizeTestCollections:false, but the
// net10.0 + xunit.runner.visualstudio adapter combo did not honor it on the Linux
// CI runner (collections ran in parallel there, surfacing the race
// deterministically). This assembly-level attribute is the authoritative xunit
// switch and is always honored by the adapter, so it guarantees serialization on
// every platform. No production code is affected.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
