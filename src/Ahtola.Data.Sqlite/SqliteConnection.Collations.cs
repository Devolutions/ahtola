using Ahtola.Core;

namespace Ahtola.Data.Sqlite;

public partial class SqliteConnection
{
    private readonly Dictionary<string, CollationRegistration> _collations = new(StringComparer.OrdinalIgnoreCase);

    private void RegisterCollation(string name, Func<string, string, int>? comparison)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (!Capabilities.SupportsCustomCollations)
            throw new NotSupportedException("Custom collations are supported only for local database connections.");
            // Shared-memory catalogs are process-wide for the named database. Collation
            // registrations are therefore catalog-scoped (visible to every lease), which is
            // what EF Core needs when multiple connections share Mode=Memory;Cache=Shared.
            if (comparison is null)
        {
            _collations.Remove(name);
            if (IsManagedConnection)
                ManagedConnection.UnregisterCollation(name);
            else if (_database is not null)
                SqliteNativeProvider.Current.UnregisterCollation(NativeDatabase, name);
            return;
        }

        var registration = new CollationRegistration(name, comparison);
        _collations[name] = registration;
        if (IsManagedConnection)
            registration.RegisterManaged(ManagedConnection);
        else if (_database is not null)
            registration.RegisterNative(NativeDatabase);
    }

    private void RegisterCollations()
    {
        if (IsManagedConnection)
        {
            foreach (var registration in _collations.Values)
                registration.RegisterManaged(ManagedConnection);
            return;
        }

        foreach (var registration in _collations.Values)
            registration.RegisterNative(NativeDatabase);
    }

    private sealed class CollationRegistration(string name, Func<string, string, int> compare)
    {
        public int Compare(string left, string right) => compare(left, right);

        public void RegisterManaged(IManagedConnectionAdapter connection)
        {
            ArgumentNullException.ThrowIfNull(connection);
            connection.RegisterCollation(name, InvokeManaged);
        }

        public void RegisterNative(AhtolaNativeDatabase database)
        {
            ArgumentNullException.ThrowIfNull(database);
            SqliteNativeProvider.Current.RegisterCollation(database, name, Compare);
        }

        private int InvokeManaged(string left, string right)
        {
            try
            {
                return Compare(left, right);
            }
            catch (Exception ex)
            {
                throw ToManagedCallbackException(ex);
            }
        }
    }
}
