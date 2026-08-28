using System.Reflection;
using AwesomeAssertions;

namespace Ahtola.Tests;

public sealed class ManagedSurfaceContractCoverageTests
{
    private static readonly ContractMapping[] Mappings =
    [
        new("composite/null primary-key predicate", typeof(ManagedReplicaLogicalReplayerTests), nameof(ManagedReplicaLogicalReplayerTests.DeleteWithANullPrimaryKeyValueMatchesTheNullRowNullSafely), "turso-src/sync/engine/src/database_replay_generator.rs::identity_predicate"),
        new("shadowed rowid predicate", typeof(ManagedReplicaLogicalReplayerTests), nameof(ManagedReplicaLogicalReplayerTests.UpsertHandlesATableWithAGenuineRowidNamedColumnThatIsNotTheAlias), "turso-src/sync/engine/src/database_replay_generator.rs::implicit_rowid_alias"),
        new("key-changing update/delete replay", typeof(ManagedReplicaLogicalReplayerTests), nameof(ManagedReplicaLogicalReplayerTests.KeyChangingReplayDeletesTheOldCompositeKeyBeforeUpsertingTheNewKey), "turso-src/sync/engine/src/database_replay_generator.rs::replay_values"),
        new("CRC stream rejection", typeof(ManagedReplicaLml3DecoderTests), nameof(ManagedReplicaLml3DecoderTests.RejectsATransactionChecksumMismatch), "turso-src/sync/engine/src/database_sync_operations.rs::apply_logical_transactions_file"),
        new("truncated stream rejection", typeof(ManagedReplicaLml3DecoderTests), nameof(ManagedReplicaLml3DecoderTests.RejectsATruncatedFrame), "turso-src/sync/engine/src/database_sync_operations.rs::apply_logical_transactions_file"),
        new("unknown stream rejection", typeof(ManagedEmbeddedReplicaConnectionTests), nameof(ManagedEmbeddedReplicaConnectionTests.CreateReplicaRejectsInvalidBootstrapStreamsWithoutInstallingFiles), "turso-src/sync/engine/src/database_sync_operations.rs::pull_updates_stream_kind"),
        new("revision metadata compatibility", typeof(ManagedEmbeddedReplicaConnectionTests), nameof(ManagedEmbeddedReplicaConnectionTests.ZeroTransactionRevisionAdvancePublishesMetadataSoTheNextSyncIsUpToDate), "turso-src/sync/engine/src/database_sync_engine.rs::update_metadata"),
        new("ack metadata compatibility", typeof(ManagedReplicaLogicalReplayerTests), nameof(ManagedReplicaLogicalReplayerTests.TransactionsAcknowledgedViaTheTursoSyncLastChangeIdFallbackAreSkipped), "turso-src/sync/engine/src/database_sync_operations.rs::logical_txn_acknowledges_client"),
        new("replace-base publication failure recovery", typeof(ManagedEmbeddedReplicaConnectionTests), nameof(ManagedEmbeddedReplicaConnectionTests.FailedPublicationReopenRecoversAPreSwapReplacementIntent), "turso-src/sync/engine/src/database_sync_engine.rs::ReplaceBaseApplyGuard"),
        new("provider state transition", typeof(ManagedProviderReaderLifecycleTests), nameof(ManagedProviderReaderLifecycleTests.ClosingAndReopeningManagedConnectionPermanentlyClosesActiveAhtolaReader), "turso-src/bindings/rust/tests/integration_tests.rs::test_invalid_transaction_state_on_rows_drop"),
        new("provider reader reuse", typeof(ManagedProviderReaderLifecycleTests), nameof(ManagedProviderReaderLifecycleTests.ManagedCommandCanBeReusedAfterItsReaderIsExhaustedAndDisposed), "turso-src/bindings/rust/tests/integration_tests.rs::test_statement_query_resets_before_execution"),
        new("provider missing parameter", typeof(ManagedCoreParameterContractRegressionTests), nameof(ManagedCoreParameterContractRegressionTests.ManagedSqliteFacadeBindsTypedNumberedNamedAndPositionalValuesAfterRebind), "turso-src/bindings/go/driver_db_test.go::TestQueryMissingParameterReturnsError"),
        new("provider interruption", typeof(ManagedProviderAsyncParityTests), nameof(ManagedProviderAsyncParityTests.ManagedSqliteCancelInterruptsActiveCommand), "turso-src/bindings/python/tests/test_interrupt.py::test_interrupt_from_watchdog_thread_aborts_query"),
        new("provider read-only", typeof(ForeignReadOnlyOpenTests), nameof(ForeignReadOnlyOpenTests.ForeignReadOnlyRejectsWrites), "turso-src/bindings/rust/tests/integration_tests.rs::test_builder_read_only_rejects_writes_without_modifying_files"),
        new("provider disposal", typeof(ManagedProviderAsyncParityTests), nameof(ManagedProviderAsyncParityTests.ManagedSqliteReaderAsyncOperationsReturnFaultedTasksAfterDisposal), "turso-src/bindings/rust/tests/integration_tests.rs::test_invalid_transaction_state_on_rows_drop"),
        new("provider concurrent execution", typeof(ManagedProviderAsyncParityTests), nameof(ManagedProviderAsyncParityTests.ManagedSqliteAsyncLockWaitIsCancellable), "turso-src/bindings/rust/tests/integration_tests.rs::test_concurrent_unique_constraint_regression"),
    ];

    [TestCaseSource(nameof(CoverageCases))]
    public void HarvestedTursoContractMapsToAnExecutableManagedCase(ContractMapping mapping)
    {
        mapping.UpstreamReference.Should().StartWith("turso-src/");
        var method = mapping.TestType.GetMethod(
            mapping.TestMethod,
            BindingFlags.Instance | BindingFlags.Public);
        method.Should().NotBeNull($"{mapping.Contract} must map to an existing executable test");
        method!.GetCustomAttributes(inherit: false)
            .Any(attribute => attribute is TestAttribute or TestCaseAttribute or TestCaseSourceAttribute)
            .Should().BeTrue($"{mapping.TestType.Name}.{mapping.TestMethod} must remain executable");
    }

    private static IEnumerable<TestCaseData> CoverageCases()
        => Mappings.Select(mapping => new TestCaseData(mapping).SetName($"Turso contract: {mapping.Contract}"));

    public sealed record ContractMapping(
        string Contract,
        Type TestType,
        string TestMethod,
        string UpstreamReference)
    {
        public override string ToString() => Contract;
    }
}
