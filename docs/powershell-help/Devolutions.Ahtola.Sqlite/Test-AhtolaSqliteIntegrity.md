---
document type: cmdlet
external help file: Devolutions.Ahtola.PowerShell.dll-Help.xml
HelpUri: ''
Locale: en-US
Module Name: Devolutions.Ahtola.Sqlite
ms.date: 08/14/2026
PlatyPS schema version: 2024-05-01
title: Test-AhtolaSqliteIntegrity
---

# Test-AhtolaSqliteIntegrity

## SYNOPSIS

Runs SQLite `PRAGMA integrity_check`.

## SYNTAX

### __AllParameterSets

```
Test-AhtolaSqliteIntegrity -Connection <SqliteConnection> [-CommandTimeout <int>] [-As <string>]
```

## ALIASES

This cmdlet has no aliases.

## DESCRIPTION

Executes `PRAGMA integrity_check` against a local connection and writes the result in the selected format. A normal result contains `ok`.

## EXAMPLES

### Example 1

This example uses a caller-owned connection and closes it when complete.

```powershell
$connection = New-AhtolaSqliteConnection
try {
    Test-AhtolaSqliteIntegrity -Connection $connection
} finally { $connection | Close-AhtolaSqliteConnection -Confirm:$false }
```

## PARAMETERS

### -As

Selects an output representation supported by this cmdlet. `PSCustomObject` is the pipeline-friendly choice.

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

### -CommandTimeout

Command timeout in seconds. Zero is allowed; the default is 30 seconds.

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
  ValueFromPipeline: true
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

### Ahtola.Data.Sqlite.SqliteConnection

A value can be piped to the pipeline-enabled parameter.

## OUTPUTS

### System.Object

Integrity-check results in the selected output format.

## NOTES

The supplied connection is caller-owned and is not closed.

## RELATED LINKS

[PowerShell module guide](https://github.com/Devolutions/ahtola/blob/master/docs/powershell-module.md)
