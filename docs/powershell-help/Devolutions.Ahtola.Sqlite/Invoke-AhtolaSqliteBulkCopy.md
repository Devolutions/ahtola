---
document type: cmdlet
external help file: Devolutions.Ahtola.PowerShell.dll-Help.xml
HelpUri: ''
Locale: en-US
Module Name: Devolutions.Ahtola.Sqlite
ms.date: 08/14/2026
PlatyPS schema version: 2024-05-01
title: Invoke-AhtolaSqliteBulkCopy
---

# Invoke-AhtolaSqliteBulkCopy

## SYNOPSIS

Bulk inserts piped objects into a local SQLite table.

## SYNTAX

### __AllParameterSets

```
Invoke-AhtolaSqliteBulkCopy -InputObject <Object> -Connection <SqliteConnection> -Table <string>
 [-BatchSize <int>] [-Transaction <SqliteTransaction>] [-WhatIf] [-Confirm]
```

## ALIASES

This cmdlet has no aliases.

## DESCRIPTION

Buffers at most `BatchSize` pipeline rows, converts them to rows, and inserts them as the pipeline is consumed. Without `Transaction`, it owns one transaction for the complete pipeline and rolls it back on failure; with a caller transaction, it uses one savepoint and rolls all bulk-copy rows back on failure.

## EXAMPLES

### Example 1

This example uses a caller-owned connection and closes it when complete.

```powershell
$connection = New-AhtolaSqliteConnection
try {
    Invoke-AhtolaSqliteQuery -Connection $connection -CommandText 'CREATE TABLE Items(Id INTEGER, Name TEXT);' -As NonQuery
    [pscustomobject]@{ Id = 1; Name = 'One' } | Invoke-AhtolaSqliteBulkCopy -Connection $connection -Table Items -Confirm:$false
} finally { $connection | Close-AhtolaSqliteConnection -Confirm:$false }
```

## PARAMETERS

### -BatchSize

Maximum number of pipeline rows retained before they are executed and discarded. Valid values are from 1 through 100000. The batch size bounds cmdlet memory use; it does not split the operation into separate commits.

```yaml
Type: System.Int32
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

### -Confirm

Prompts you for confirmation before running the cmdlet.

```yaml
Type: System.Management.Automation.SwitchParameter
DefaultValue: ''
SupportsWildcards: false
Aliases:
- cf
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

### -Connection

Caller-owned Ahtola connection. It can be opened if needed but is never closed or disposed by this cmdlet.

```yaml
Type: Ahtola.Data.Sqlite.SqliteConnection
DefaultValue: ''
SupportsWildcards: false
Aliases:
- SqliteConnection
ParameterSets:
- Name: (All)
  Position: Named
  IsRequired: true
  ValueFromPipeline: false
  ValueFromPipelineByPropertyName: false
  ValueFromRemainingArguments: false
DontShow: false
AcceptedValues: []
HelpMessage: ''
```

### -InputObject

A `DataRow`, dictionary, or object with readable properties to insert. All batch rows need the same columns.

```yaml
Type: System.Object
DefaultValue: ''
SupportsWildcards: false
Aliases: []
ParameterSets:
- Name: (All)
  Position: Named
  IsRequired: true
  ValueFromPipeline: true
  ValueFromPipelineByPropertyName: false
  ValueFromRemainingArguments: false
DontShow: false
AcceptedValues: []
HelpMessage: ''
```

### -Table

SQLite table name. The cmdlet quotes the identifier; this is not a SQL fragment.

```yaml
Type: System.String
DefaultValue: ''
SupportsWildcards: false
Aliases:
- TableName
ParameterSets:
- Name: (All)
  Position: Named
  IsRequired: true
  ValueFromPipeline: false
  ValueFromPipelineByPropertyName: false
  ValueFromRemainingArguments: false
DontShow: false
AcceptedValues: []
HelpMessage: ''
```

### -Transaction

Caller-owned active transaction. It must belong to the supplied connection when both are present.

```yaml
Type: Ahtola.Data.Sqlite.SqliteTransaction
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

### -WhatIf

Runs the command in a mode that only reports what would happen without performing the actions.

```yaml
Type: System.Management.Automation.SwitchParameter
DefaultValue: ''
SupportsWildcards: false
Aliases:
- wi
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

### System.Object

A value can be piped to the pipeline-enabled parameter.

## OUTPUTS

### System.Int32

The number of rows inserted.

## NOTES

Supports `-WhatIf` and `-Confirm`. All rows must expose the same column names. A later failed row rolls back rows that were already flushed from earlier batches.

## RELATED LINKS

[PowerShell module guide](https://github.com/Devolutions/ahtola/blob/master/docs/powershell-module.md)
