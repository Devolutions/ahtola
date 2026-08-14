---
document type: cmdlet
external help file: Devolutions.Ahtola.PowerShell.dll-Help.xml
HelpUri: ''
Locale: en-US
Module Name: Devolutions.Ahtola.Sqlite
ms.date: 08/14/2026
PlatyPS schema version: 2024-05-01
title: Compare-AhtolaSqliteDatabaseVersion
---

# Compare-AhtolaSqliteDatabaseVersion

## SYNOPSIS

Compares a configured database metadata version with an expected version.

## SYNTAX

### __AllParameterSets

```
Compare-AhtolaSqliteDatabaseVersion [-Configuration] <SQLiteDBConfig> [-ExpectedVersion <string>]
```

## ALIASES

This cmdlet has no aliases.

## DESCRIPTION

Reports deployed and expected versions, comparison direction, deployment state, and reasons. The expected version defaults to `Configuration.Version`.

## EXAMPLES

### Example 1

This example uses a caller-owned connection and closes it when complete.

```powershell
$config = [Devolutions.Ahtola.Sqlite.SQLiteDBConfig]::new('Data Source=app.db')
Compare-AhtolaSqliteDatabaseVersion -Configuration $config -ExpectedVersion '1'
```

## PARAMETERS

### -Configuration

A `SQLiteDBConfig` that supplies the connection string and settings for this operation.

```yaml
Type: Ahtola.PSSqlite.SQLiteDBConfig
DefaultValue: ''
SupportsWildcards: false
Aliases:
- DatabaseConfig
- SqliteDBConfig
ParameterSets:
- Name: (All)
  Position: 0
  IsRequired: true
  ValueFromPipeline: false
  ValueFromPipelineByPropertyName: false
  ValueFromRemainingArguments: false
DontShow: false
AcceptedValues: []
HelpMessage: ''
```

### -ExpectedVersion

Version to compare with stored metadata. If omitted, `Configuration.Version` is used.

```yaml
Type: System.String
DefaultValue: ''
SupportsWildcards: false
Aliases: []
ParameterSets:
- Name: (All)
  Position: Named
  IsRequired: false
  ValueFromPipeline: false
  ValueFromPipelineByPropertyName: false
  ValueFromRemainingArguments: false
DontShow: false
AcceptedValues: []
HelpMessage: ''
```

### CommonParameters

This cmdlet supports the common parameters: -Debug, -ErrorAction, -ErrorVariable,
-InformationAction, -InformationVariable, -OutBuffer, -OutVariable, -PipelineVariable,
-ProgressAction, -Verbose, -WarningAction, and -WarningVariable. For more information, see
[about_CommonParameters](https://go.microsoft.com/fwlink/?LinkID=113216).

## INPUTS

None. This cmdlet does not accept pipeline input.

## OUTPUTS

### Ahtola.PSSqlite.DBVersionComparisonResult

A `DBVersionComparisonResult` with the comparison outcome.

## NOTES

This command does not change the database. Missing databases or metadata are reported in the result.

## RELATED LINKS

[PowerShell module guide](https://github.com/Devolutions/ahtola/blob/master/docs/powershell-module.md)
