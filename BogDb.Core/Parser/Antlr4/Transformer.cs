using System;
using System.Collections.Generic;
using Antlr4.Runtime;
using Antlr4.Runtime.Tree;
using System.Linq;
using System.IO;
using BogDb.Core.Parser.Query;
using BogDb.Core.Common;

namespace BogDb.Core.Parser.Antlr4;

/// <summary>
/// Transformer traverses the ANTLR4 generated Context Parse Trees and converts them into
/// Strongly Typed C# AST Statements and Expressions ready for the semantic Binder.
/// Phase 8: Full MATCH / RETURN / WHERE / expression pipeline.
/// </summary>
public class Transformer
{
    private readonly CypherParser.Ku_StatementsContext _root;
    private readonly IReadOnlyList<string> _callSubqueryBodies;

    public Transformer(CypherParser.Ku_StatementsContext root)
        : this(root, Array.Empty<string>()) { }

    public Transformer(CypherParser.Ku_StatementsContext root, IReadOnlyList<string> callSubqueryBodies)
    {
        _root = root;
        _callSubqueryBodies = callSubqueryBodies;
    }

    // ─── Top-level ─────────────────────────────────────────────────────────────

    public List<Statement> Transform()
    {
        var statements = new List<Statement>();
        foreach (var cypherContext in _root.oC_Cypher())
        {
            var statementContext = cypherContext.oC_Statement();
            if (statementContext != null)
            {
                var statement = TransformStatement(statementContext);
                statement = ApplyCypherOption(statement, cypherContext);
                statements.Add(statement);
            }
        }
        return statements;
    }

    private static Statement ApplyCypherOption(Statement statement, CypherParser.OC_CypherContext ctx)
    {
        var option = ctx.oC_AnyCypherOption();
        if (option == null)
            return statement;

        if (statement is not RegularQuery regularQuery)
            throw new NotSupportedException($"Cypher query option is not supported for statement: {ctx.GetText()}");

        if (option.oC_Profile() != null)
        {
            regularQuery.SetIsProfile(true);
            return regularQuery;
        }

        if (option.oC_Explain() != null)
        {
            var explainType = option.oC_Explain().LOGICAL() != null
                ? ExplainType.LOGICAL_PLAN
                : ExplainType.PHYSICAL_PLAN;
            return new ExplainStatement(regularQuery, explainType);
        }

        return statement;
    }

    public Statement TransformStatement(CypherParser.OC_StatementContext ctx)
    {
        if (ctx.oC_Query() != null)
            return TransformQuery(ctx.oC_Query());
        if (ctx.kU_Transaction() != null)
            return TransformTransaction(ctx.kU_Transaction());
        if (ctx.kU_CopyFrom() != null)
            return TransformCopyFrom(ctx.kU_CopyFrom());
        if (ctx.kU_CopyTO() != null)
            return TransformCopyTo(ctx.kU_CopyTO());
        if (ctx.kU_AttachDatabase() != null)
            return TransformAttachDatabase(ctx.kU_AttachDatabase());
        if (ctx.kU_DetachDatabase() != null)
            return TransformDetachDatabase(ctx.kU_DetachDatabase());
        if (ctx.kU_UseDatabase() != null)
            return TransformUseDatabase(ctx.kU_UseDatabase());

        if (ctx.kU_CreateNodeTable() != null)
            return TransformCreateNodeTable(ctx.kU_CreateNodeTable());
        if (ctx.kU_CreateRelTable() != null)
            return TransformCreateRelTable(ctx.kU_CreateRelTable());
        if (ctx.kU_Drop() != null)
            return TransformDrop(ctx.kU_Drop());
        if (ctx.kU_AlterTable() != null)
            return TransformAlterTable(ctx.kU_AlterTable());
        if (ctx.kU_CreateMacro() != null)
            return TransformCreateMacro(ctx.kU_CreateMacro());
        if (ctx.kU_StandaloneCall() != null)
            return TransformStandaloneCall(ctx.kU_StandaloneCall());
        if (ctx.kU_Extension() != null)
            return TransformExtension(ctx.kU_Extension());

        // Extended statement types
        if (ctx.kU_CreateSequence() != null)
            return TransformCreateSequence(ctx.kU_CreateSequence());
        if (ctx.kU_CreateType() != null)
            return TransformCreateType(ctx.kU_CreateType());
        if (ctx.kU_CommentOn() != null)
            return TransformCommentOn(ctx.kU_CommentOn());
        if (ctx.kU_ExportDatabase() != null)
            return TransformExportDatabase(ctx.kU_ExportDatabase());
        if (ctx.kU_ImportDatabase() != null)
            return TransformImportDatabase(ctx.kU_ImportDatabase());

        throw new InvalidOperationException($"Syntax error near '{ctx.GetText()}'.");
    }

    // ─── DDL ───────────────────────────────────────────────────────────────────

    private CreateNodeTable TransformCreateNodeTable(CypherParser.KU_CreateNodeTableContext ctx)
    {
        var schemaNameCtx = ctx.children.OfType<CypherParser.OC_SchemaNameContext>().FirstOrDefault();
        var tableName = schemaNameCtx != null ? schemaNameCtx.GetText() : "";
        var props = new List<DeclaredColumnDefinition>();
        if (ctx.kU_PropertyDefinitions() != null)
        {
            var defs = ctx.kU_PropertyDefinitions().children.OfType<CypherParser.KU_PropertyDefinitionContext>();
            foreach (var propCtx in defs)
            {
                var columnDef = propCtx.kU_ColumnDefinition();
                var keyName = columnDef?.oC_PropertyKeyName()?.GetText() ?? "";
                var typeName = columnDef?.kU_DataType()?.GetText() ?? "";
                props.Add(new DeclaredColumnDefinition(keyName, typeName));
            }
        }
        return new CreateNodeTable(new CreateTableInfo(tableName, props));
    }

    private CreateRelTable TransformCreateRelTable(CypherParser.KU_CreateRelTableContext ctx)
    {
        var tableName = ctx.oC_SchemaName()?.GetText() ?? "";
        var connection = ctx.kU_FromToConnections()?.kU_FromToConnection(0);
        var srcTable = connection?.oC_SchemaName(0)?.GetText() ?? "";
        var dstTable = connection?.oC_SchemaName(1)?.GetText() ?? "";

        var props = new List<DeclaredColumnDefinition>();
        if (ctx.kU_PropertyDefinitions() != null)
        {
            var defs = ctx.kU_PropertyDefinitions().children.OfType<CypherParser.KU_PropertyDefinitionContext>();
            foreach (var propCtx in defs)
            {
                var columnDef = propCtx.kU_ColumnDefinition();
                var keyName = columnDef?.oC_PropertyKeyName()?.GetText() ?? "";
                var typeName = columnDef?.kU_DataType()?.GetText() ?? "";
                props.Add(new DeclaredColumnDefinition(keyName, typeName));
            }
        }

        return new CreateRelTable(new CreateTableInfo(tableName, props), srcTable, dstTable);
    }

    private DropTable TransformDrop(CypherParser.KU_DropContext ctx)
    {
        var schemaNameCtx = ctx.children.OfType<CypherParser.OC_SchemaNameContext>().FirstOrDefault();
        return new DropTable(schemaNameCtx != null ? schemaNameCtx.GetText() : "");
    }

    private AlterTable TransformAlterTable(CypherParser.KU_AlterTableContext ctx)
    {
        var schemaNameCtx = ctx.children.OfType<CypherParser.OC_SchemaNameContext>().FirstOrDefault();
        var tableName = schemaNameCtx != null ? schemaNameCtx.GetText() : "";
        var options = ctx.kU_AlterOptions();

        if (options.kU_AddProperty() != null)
        {
            var addProperty = options.kU_AddProperty();
            var propertyName = addProperty.oC_PropertyKeyName()?.GetText() ?? "";
            var typeName = addProperty.kU_DataType()?.GetText() ?? "";
            var defaultExpression = addProperty.kU_Default() != null
                ? TransformExpression(addProperty.kU_Default().oC_Expression())
                : null;
            return new AlterTableAddProperty(tableName, propertyName, typeName, defaultExpression);
        }

        if (options.kU_DropProperty() != null)
        {
            var dropProperty = options.kU_DropProperty();
            var propertyName = dropProperty.oC_PropertyKeyName()?.GetText() ?? "";
            return new AlterTableDropProperty(tableName, propertyName);
        }

        if (options.kU_RenameTable() != null)
        {
            var renameTable = options.kU_RenameTable();
            var newTableName = renameTable.oC_SchemaName()?.GetText() ?? "";
            return new AlterTableRenameTable(tableName, newTableName);
        }

        if (options.kU_RenameProperty() != null)
        {
            var renameProperty = options.kU_RenameProperty();
            var propertyNames = renameProperty.oC_PropertyKeyName();
            var oldPropertyName = propertyNames.Length > 0 ? propertyNames[0].GetText() : "";
            var newPropertyName = propertyNames.Length > 1 ? propertyNames[1].GetText() : "";
            return new AlterTableRenameProperty(tableName, oldPropertyName, newPropertyName);
        }

        if (options.kU_AddFromToConnection() != null)
        {
            var addConnection = options.kU_AddFromToConnection();
            var connection = addConnection.kU_FromToConnection();
            return new AlterTableConnectionChange(
                tableName,
                isAdd: true,
                ignoreIfPresentOrMissing: addConnection.kU_IfNotExists() != null,
                connection.oC_SchemaName(0)?.GetText() ?? "",
                connection.oC_SchemaName(1)?.GetText() ?? "");
        }

        if (options.kU_DropFromToConnection() != null)
        {
            var dropConnection = options.kU_DropFromToConnection();
            var connection = dropConnection.kU_FromToConnection();
            return new AlterTableConnectionChange(
                tableName,
                isAdd: false,
                ignoreIfPresentOrMissing: dropConnection.kU_IfExists() != null,
                connection.oC_SchemaName(0)?.GetText() ?? "",
                connection.oC_SchemaName(1)?.GetText() ?? "");
        }

        throw new InvalidOperationException($"Syntax error near '{ctx.GetText()}'.");
    }

    private Statement TransformStandaloneCall(CypherParser.KU_StandaloneCallContext ctx)
    {
        if (ctx.oC_FunctionInvocation() != null)
        {
            var funcExpr = TransformFunctionInvocation(ctx.oC_FunctionInvocation());
            return new StandaloneCallFunction(funcExpr);
        }

        var optionName = ctx.oC_SymbolicName()?.GetText() ?? "";
        var optionValue = TransformExpression(ctx.oC_Expression());
        return new StandaloneCall(optionName, optionValue);
    }

    private Statement TransformExtension(CypherParser.KU_ExtensionContext ctx)
    {
        if (ctx.kU_LoadExtension() != null)
        {
            var load = ctx.kU_LoadExtension();
            var target = load.StringLiteral() != null
                ? TransformStringLiteral(load.StringLiteral())
                : load.oC_Variable()?.GetText() ?? string.Empty;
            return new ExtensionStatement(ExtensionCommand.LOAD, target);
        }

        if (ctx.kU_InstallExtension() != null)
        {
            var install = ctx.kU_InstallExtension();
            var target = install.oC_Variable()?.GetText() ?? string.Empty;
            var repo = install.StringLiteral() != null ? TransformStringLiteral(install.StringLiteral()) : null;
            return new ExtensionStatement(
                ExtensionCommand.INSTALL,
                target,
                forceInstall: install.FORCE() != null,
                repositoryPath: repo);
        }

        if (ctx.kU_UninstallExtension() != null)
        {
            var uninstall = ctx.kU_UninstallExtension();
            return new ExtensionStatement(
                ExtensionCommand.UNINSTALL,
                uninstall.oC_Variable()?.GetText() ?? string.Empty);
        }

        if (ctx.kU_UpdateExtension() != null)
        {
            var update = ctx.kU_UpdateExtension();
            return new ExtensionStatement(
                ExtensionCommand.UPDATE,
                update.oC_Variable()?.GetText() ?? string.Empty);
        }

        throw new InvalidOperationException($"Syntax error near '{ctx.GetText()}'.");
    }

    private CreateMacro TransformCreateMacro(CypherParser.KU_CreateMacroContext ctx)
    {
        var macroName = TransformFunctionName(ctx.oC_FunctionName());
        var parameters = new List<MacroParameter>();

        if (ctx.kU_PositionalArgs() != null)
        {
            foreach (var positionalArg in ctx.kU_PositionalArgs().oC_SymbolicName())
                parameters.Add(new MacroParameter(TransformSymbolicName(positionalArg)));
        }

        foreach (var defaultArg in ctx.kU_DefaultArg())
        {
            var parameterName = TransformSymbolicName(defaultArg.oC_SymbolicName());
            var defaultExpression = TransformLiteral(defaultArg.oC_Literal());
            parameters.Add(new MacroParameter(parameterName, defaultExpression));
        }

        return new CreateMacro(macroName, parameters, TransformExpression(ctx.oC_Expression()));
    }

    // ─── Query ─────────────────────────────────────────────────────────────────

    public Statement TransformQuery(CypherParser.OC_QueryContext ctx)
    {
        if (ctx.oC_RegularQuery() != null)
            return TransformRegularQuery(ctx.oC_RegularQuery());

        if (ctx.kU_CallQuery() != null)
        {
            var callCtx = ctx.kU_CallQuery();
            var singleQuery = new SingleQuery();
            singleQuery.AddReadingClause(TransformInQueryCall(callCtx.kU_InQueryCall()));
            singleQuery.SetReturnClause(TransformReturn(callCtx.oC_Return()));
            return new RegularQuery(singleQuery);
        }

        throw new InvalidOperationException($"Syntax error near '{ctx.GetText()}'.");
    }

    private Statement TransformRegularQuery(CypherParser.OC_RegularQueryContext ctx)
    {
        var firstSingle = TransformSingleQuery(ctx.oC_SingleQuery());
        var regularQuery = new RegularQuery(firstSingle);
        foreach (var unionCtx in ctx.oC_Union())
        {
            var unionQuery = TransformSingleQuery(unionCtx.oC_SingleQuery());
            regularQuery.AddSingleQuery(unionQuery, isUnionAll: unionCtx.ALL() != null);
        }
        return regularQuery;
    }

    private SingleQuery TransformSingleQuery(CypherParser.OC_SingleQueryContext ctx)
    {
        var singleQuery = new SingleQuery();
        if (ctx.oC_MultiPartQuery() != null)
        {
            var multiCtx = ctx.oC_MultiPartQuery();
            for (var i = 0; i < multiCtx.ChildCount; i++)
            {
                var child = multiCtx.GetChild(i);
                if (child is CypherParser.KU_QueryPartContext qp)
                {
                    foreach (var rc in qp.oC_ReadingClause())
                        singleQuery.AddReadingClause(TransformReadingClause(rc));
                    foreach (var uc in qp.oC_UpdatingClause())
                        singleQuery.AddUpdatingClause(TransformUpdatingClause(uc));
                    if (qp.oC_With() != null)
                        singleQuery.AddReadingClause(TransformWith(qp.oC_With()));
                }
                else if (child is CypherParser.OC_SinglePartQueryContext spq)
                {
                    foreach (var spqRc in spq.oC_ReadingClause())
                        singleQuery.AddReadingClause(TransformReadingClause(spqRc));
                    foreach (var spqUc in spq.oC_UpdatingClause())
                        singleQuery.AddUpdatingClause(TransformUpdatingClause(spqUc));
                    if (spq.oC_Return() != null)
                        singleQuery.SetReturnClause(TransformReturn(spq.oC_Return()));
                }
            }
        }
        else
        {
            return TransformSinglePartQuery(ctx.oC_SinglePartQuery());
        }
        return singleQuery;
    }

    private SingleQuery TransformSinglePartQuery(CypherParser.OC_SinglePartQueryContext ctx)
    {
        var singleQuery = new SingleQuery();
        if (ctx.oC_ReadingClause() != null)
        {
            foreach (var readingClauseCtx in ctx.oC_ReadingClause())
                singleQuery.AddReadingClause(TransformReadingClause(readingClauseCtx));
        }
        if (ctx.oC_UpdatingClause() != null)
        {
            foreach (var updatingClauseCtx in ctx.oC_UpdatingClause())
                singleQuery.AddUpdatingClause(TransformUpdatingClause(updatingClauseCtx));
        }
        if (ctx.oC_Return() != null)
            singleQuery.SetReturnClause(TransformReturn(ctx.oC_Return()));
        return singleQuery;
    }

    // ─── Reading Clauses ───────────────────────────────────────────────────────

    private ReadingClause TransformReadingClause(CypherParser.OC_ReadingClauseContext ctx)
    {
        if (ctx.oC_Match() != null)
        {
            // Check if this is a placeholder MATCH for a CALL { subquery } block
            var matchCtx = ctx.oC_Match();
            var callSubquery = TryTransformCallSubqueryPlaceholder(matchCtx);
            if (callSubquery != null)
                return callSubquery;

            return TransformMatch(matchCtx);
        }
        if (ctx.oC_Unwind() != null)
            return TransformUnwind(ctx.oC_Unwind());
        if (ctx.kU_InQueryCall() != null)
            return TransformInQueryCall(ctx.kU_InQueryCall());
        if (ctx.kU_LoadFrom() != null)
            return TransformLoadFrom(ctx.kU_LoadFrom());
        throw new InvalidOperationException($"Syntax error near '{ctx.GetText()}'.");
    }

    /// <summary>
    /// Detects placeholder MATCH patterns inserted by CallSubqueryPreprocessor.
    /// Pattern: MATCH (__call_subquery_N:__CallSubquery)
    /// </summary>
    private ParsedCallSubquery? TryTransformCallSubqueryPlaceholder(CypherParser.OC_MatchContext matchCtx)
    {
        if (_callSubqueryBodies.Count == 0)
            return null;

        // Check if the match has exactly one pattern part with one node and no relationships
        var pattern = matchCtx.oC_Pattern();
        if (pattern == null) return null;

        var parts = pattern.oC_PatternPart();
        if (parts == null || parts.Length != 1) return null;

        var anonPart = parts[0].oC_AnonymousPatternPart();
        if (anonPart == null) return null;

        var element = anonPart.oC_PatternElement();
        if (element == null) return null;

        // Must have exactly one node and no chains
        if (element.oC_PatternElementChain() != null && element.oC_PatternElementChain().Length > 0)
            return null;

        var nodePattern = element.oC_NodePattern();
        if (nodePattern == null) return null;

        // Check variable name starts with placeholder prefix
        var varCtx = nodePattern.oC_Variable();
        if (varCtx == null) return null;

        var varName = varCtx.GetText();
        if (!CallSubqueryPreprocessor.IsPlaceholder(varName))
            return null;

        // Check label is __CallSubquery
        var labels = nodePattern.oC_NodeLabels();
        if (labels == null) return null;

        var labelNames = labels.oC_LabelName();
        if (labelNames == null || labelNames.Length != 1) return null;

        if (labelNames[0].GetText() != CallSubqueryPreprocessor.PlaceholderLabel)
            return null;

        // Extract and parse the inner query body
        int index = CallSubqueryPreprocessor.GetPlaceholderIndex(varName);
        if (index < 0 || index >= _callSubqueryBodies.Count)
            throw new InvalidOperationException($"Invalid CALL subquery placeholder index: {index}");

        var innerQueryText = _callSubqueryBodies[index];
        var innerQuery = ParseInnerQuery(innerQueryText);
        return new ParsedCallSubquery(innerQuery, innerQueryText);
    }

    /// <summary>
    /// Parses a Cypher query string into a RegularQuery AST node.
    /// Used for CALL { subquery } inner query parsing.
    /// </summary>
    private static RegularQuery ParseInnerQuery(string queryText)
    {
        var inputStream = new AntlrInputStream(queryText);
        var lexer = new CypherLexer(inputStream);
        lexer.RemoveErrorListeners();
        var tokenStream = new CommonTokenStream(lexer);
        var parser = new CypherParser(tokenStream);
        parser.RemoveErrorListeners();
        var syntaxErrors = new List<string>();
        parser.AddErrorListener(new InnerQueryErrorListener(syntaxErrors));
        var root = parser.ku_Statements();
        if (syntaxErrors.Count > 0)
            throw new InvalidOperationException(
                $"Syntax error in CALL subquery: {string.Join("; ", syntaxErrors)}");

        // The inner query should parse as a single statement containing a RegularQuery
        var transformer = new Transformer(root);
        var statements = transformer.Transform();
        if (statements.Count != 1 || statements[0] is not RegularQuery regularQuery)
            throw new InvalidOperationException(
                "CALL subquery must contain exactly one query (MATCH ... RETURN ...).");

        return regularQuery;
    }

    /// <summary>Simple error listener for inner subquery parsing.</summary>
    private class InnerQueryErrorListener : BaseErrorListener
    {
        private readonly List<string> _errors;
        public InnerQueryErrorListener(List<string> errors) { _errors = errors; }
        public override void SyntaxError(
            System.IO.TextWriter output,
            IRecognizer recognizer, IToken offendingSymbol,
            int line, int charPositionInLine, string msg,
            RecognitionException e)
        {
            _errors.Add($"line {line}:{charPositionInLine} {msg}");
        }
    }

    private Statement TransformAttachDatabase(CypherParser.KU_AttachDatabaseContext ctx)
    {
        var path = TransformStringLiteral(ctx.StringLiteral());
        var alias = ctx.oC_SchemaName()?.GetText();
        if (string.IsNullOrWhiteSpace(alias))
            alias = Path.GetFileNameWithoutExtension(path);

        var dbType = ctx.oC_SymbolicName()?.GetText() ?? string.Empty;
        var options = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        if (ctx.kU_Options() != null)
        {
            foreach (var optionCtx in ctx.kU_Options().kU_Option())
            {
                var optionName = optionCtx.oC_SymbolicName()?.GetText() ?? string.Empty;
                if (string.IsNullOrWhiteSpace(optionName))
                    continue;

                if (optionCtx.oC_Literal() != null)
                {
                    var literal = TransformLiteral(optionCtx.oC_Literal()) as ParsedLiteralExpression;
                    options[optionName] = literal?.Value;
                }
                else
                {
                    options[optionName] = true;
                }
            }
        }

        return new AttachDatabaseStatement(path, alias ?? string.Empty, dbType, options);
    }

    private Statement TransformDetachDatabase(CypherParser.KU_DetachDatabaseContext ctx)
    {
        var dbName = ctx.oC_SchemaName()?.GetText() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(dbName))
            throw new InvalidOperationException("DETACH requires a database name.");
        return new DetachDatabaseStatement(dbName);
    }

    private Statement TransformUseDatabase(CypherParser.KU_UseDatabaseContext ctx)
    {
        var dbName = ctx.oC_SchemaName()?.GetText() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(dbName))
            throw new InvalidOperationException("USE requires a database name.");
        return new UseDatabaseStatement(dbName);
    }

    private InQueryCallClause TransformLoadFrom(CypherParser.KU_LoadFromContext ctx)
    {
        // Keep the planner-integrated slice honest: lower the plain extension-backed
        // LOAD FROM path into a regular table-function style read source. CSV
        // options still stay on the dedicated helper path, but typed column
        // definitions can now flow through the regular pipeline too.
        if (ctx.kU_Options() != null)
            throw new NotSupportedException($"Planner-integrated LOAD FROM currently does not support options: {ctx.GetText()}");

        var funcExpr = new ParsedFunctionExpression("LOAD_FROM", ctx.GetText());
        funcExpr.AddChild(TransformLoadFromSource(ctx.kU_ScanSource()));
        var yieldVariables = AppendLoadFromColumnDefinitions(funcExpr, ctx.kU_ColumnDefinitions());

        var clause = new InQueryCallClause(funcExpr, yieldVariables);
        if (ctx.oC_Where() != null)
            clause.SetWherePredicate(TransformWhere(ctx.oC_Where()));
        return clause;
    }

    private ParsedExpression TransformLoadFromSource(CypherParser.KU_ScanSourceContext ctx)
    {
        if (ctx.kU_FilePaths() != null)
        {
            var pathLiteral = ctx.kU_FilePaths().StringLiteral().FirstOrDefault();
            if (pathLiteral == null)
                throw new InvalidOperationException($"LOAD FROM file path could not be resolved: {ctx.GetText()}");
            return new ParsedLiteralExpression(TransformStringLiteral(pathLiteral), pathLiteral.GetText());
        }

        if (ctx.oC_Parameter() != null)
            return TransformParameter(ctx.oC_Parameter());
        if (ctx.oC_FunctionInvocation() != null)
            return TransformFunctionInvocation(ctx.oC_FunctionInvocation());
        if (ctx.oC_Variable() != null && ctx.oC_SchemaName() != null)
        {
            var variable = new ParsedVariableExpression(TransformVariable(ctx.oC_Variable()), ctx.oC_Variable().GetText());
            var propertyName = TransformSchemaName(ctx.oC_SchemaName());
            return new ParsedPropertyExpression(propertyName, variable, ctx.GetText());
        }
        if (ctx.oC_Variable() != null)
        {
            var variableName = TransformVariable(ctx.oC_Variable());
            return new ParsedVariableExpression(variableName, ctx.oC_Variable().GetText());
        }

        throw new NotSupportedException($"LOAD FROM source shape not yet supported in planner-integrated path: {ctx.GetText()}");
    }

    private static IReadOnlyList<ParsedExpression> AppendLoadFromColumnDefinitions(
        ParsedFunctionExpression funcExpr,
        CypherParser.KU_ColumnDefinitionsContext? columnDefinitions)
    {
        var yieldVariables = new List<ParsedExpression>();
        if (columnDefinitions == null)
            return yieldVariables;

        foreach (var columnDefinition in columnDefinitions.kU_ColumnDefinition())
        {
            var columnName = columnDefinition.oC_PropertyKeyName()?.GetText() ?? string.Empty;
            var typeName = columnDefinition.kU_DataType()?.GetText() ?? string.Empty;
            funcExpr.AddChild(new ParsedLiteralExpression(columnName, columnName, LogicalTypeID.STRING));
            funcExpr.AddChild(new ParsedLiteralExpression(typeName, typeName, LogicalTypeID.STRING));
            yieldVariables.Add(new ParsedVariableExpression(columnName, columnName));
        }

        return yieldVariables;
    }

    private InQueryCallClause TransformInQueryCall(CypherParser.KU_InQueryCallContext ctx)
    {
        var funcExpr = TransformFunctionInvocation(ctx.oC_FunctionInvocation());
        var yieldVars = new List<ParsedExpression>();
        if (ctx.oC_YieldItems() != null)
        {
            foreach (var item in ctx.oC_YieldItems().oC_YieldItem())
            {
                var vars = item.oC_Variable();
                // YIELD * — oC_Variable() is null for STAR items; skip (means "yield all")
                if (vars == null || vars.Length == 0) continue;
                var resolvedVar = TransformVariable(vars[vars.Length - 1]);
                var varExpr = new ParsedVariableExpression(resolvedVar, resolvedVar);
                yieldVars.Add(varExpr);
            }
        }
        var clause = new InQueryCallClause(funcExpr, yieldVars);
        if (ctx.oC_Where() != null)
        {
            clause.SetWherePredicate(TransformWhere(ctx.oC_Where()));
        }
        return clause;
    }

    private UnwindClause TransformUnwind(CypherParser.OC_UnwindContext ctx)
    {
        var expr = TransformExpression(ctx.oC_Expression());
        var alias = TransformVariable(ctx.oC_Variable());
        return new UnwindClause(expr, alias);
    }

    private WithClause TransformWith(CypherParser.OC_WithContext ctx)
    {
        var proj = TransformProjectionBody(ctx.oC_ProjectionBody());
        ParsedExpression? wherePred = null;
        if (ctx.oC_Where() != null)
            wherePred = TransformWhere(ctx.oC_Where());
        return new WithClause(proj, wherePred);
    }

    private MatchClause TransformMatch(CypherParser.OC_MatchContext ctx)
    {
        var clauseType = ctx.OPTIONAL() != null ? ClauseType.OPTIONAL_MATCH : ClauseType.MATCH;
        var patterns = TransformPattern(ctx.oC_Pattern());
        var matchClause = new MatchClause(patterns, clauseType);
        if (ctx.oC_Where() != null)
            matchClause.SetWherePredicate(TransformWhere(ctx.oC_Where()));
        return matchClause;
    }

    // ─── Updating Clauses ──────────────────────────────────────────────────────
    
    private UpdatingClause TransformUpdatingClause(CypherParser.OC_UpdatingClauseContext ctx)
    {
        if (ctx.oC_Create() != null) return TransformCreate(ctx.oC_Create());
        if (ctx.oC_Merge() != null) return TransformMerge(ctx.oC_Merge());
        if (ctx.oC_Delete() != null) return TransformDelete(ctx.oC_Delete());
        if (ctx.oC_Set() != null) return TransformSet(ctx.oC_Set());
        throw new InvalidOperationException($"Internal error: unreachable updating clause branch: {ctx.GetText()}");
    }

    private CreateClause TransformCreate(CypherParser.OC_CreateContext ctx)
    {
        var patterns = TransformPattern(ctx.oC_Pattern());
        return new CreateClause(patterns);
    }

    private MergeClause TransformMerge(CypherParser.OC_MergeContext ctx)
    {
        var patterns = TransformPattern(ctx.oC_Pattern());
        
        var actions = new List<MergeAction>();
        if (ctx.oC_MergeAction() != null)
        {
            foreach (var actionCtx in ctx.oC_MergeAction())
            {
                var isOnMatch = actionCtx.MATCH() != null;
                var setClause = TransformSet(actionCtx.oC_Set());
                actions.Add(new MergeAction(isOnMatch, setClause));
            }
        }
        
        return new MergeClause(patterns, actions);
    }

    private DeleteClause TransformDelete(CypherParser.OC_DeleteContext ctx)
    {
        var exprs = new List<ParsedExpression>();
        foreach (var exprCtx in ctx.oC_Expression())
            exprs.Add(TransformExpression(exprCtx));
        return new DeleteClause(exprs);
    }

    private SetClause TransformSet(CypherParser.OC_SetContext ctx)
    {
        var setItems = new List<ParsedExpression>();
        if (ctx.oC_SetItem() != null && ctx.oC_SetItem().Length > 0)
        {
            foreach (var setCtx in ctx.oC_SetItem())
            {
                var left = TransformPropertyExpression(setCtx.oC_PropertyExpression());
                var right = TransformExpression(setCtx.oC_Expression());
                setItems.Add(new StructuralParsedExpression(ExpressionType.EQUALS, left, right, setCtx.GetText()));
            }
        }
        else if (ctx.oC_Atom() != null)
        {
            var mapTarget = TransformAtom(ctx.oC_Atom());
            if (ctx.kU_Properties()?.oC_Parameter() != null)
            {
                if (mapTarget is not ParsedVariableExpression)
                    throw new NotSupportedException($"SET property bag shorthand requires a variable target: {ctx.GetText()}");

                var propertyBag = TransformPropertyBagIfPresent(ctx.kU_Properties())
                    ?? throw new InvalidOperationException($"SET property bag shorthand could not be transformed: {ctx.GetText()}");
                setItems.Add(new StructuralParsedExpression(ExpressionType.EQUALS, mapTarget, propertyBag, ctx.GetText()));
                return new SetClause(setItems);
            }

            // SET n = {k1: v1, k2: v2} -> SET n.k1 = v1, n.k2 = v2
            var properties = TransformPropertyEntriesIfPresent(ctx.kU_Properties());
            foreach (var (key, value) in properties)
            {
                var left = new ParsedPropertyExpression(
                    key,
                    mapTarget.Copy(),
                    $"{mapTarget.GetRawName()}.{key}");
                var raw = $"{left.GetRawName()} = {value.GetRawName()}";
                setItems.Add(new StructuralParsedExpression(ExpressionType.EQUALS, left, value, raw));
            }
        }
        return new SetClause(setItems);
    }

    private ParsedExpression TransformPropertyExpression(CypherParser.OC_PropertyExpressionContext ctx)
    {
        var atom = TransformAtom(ctx.oC_Atom());
        var lookup = ctx.oC_PropertyLookup();
        if (lookup != null)
        {
            var propName = lookup.STAR() != null
                ? "*"
                : TransformSchemaName(lookup.oC_PropertyKeyName().oC_SchemaName());
            var raw = atom.ToString() + "." + propName;
            atom = new ParsedPropertyExpression(propName, atom, raw);
        }
        return atom;
    }

    // ─── Graph Patterns ────────────────────────────────────────────────────────

    private List<PatternElement> TransformPattern(CypherParser.OC_PatternContext ctx)
    {
        var patterns = new List<PatternElement>();
        foreach (var partCtx in ctx.oC_PatternPart())
            patterns.Add(TransformPatternPart(partCtx));
        return patterns;
    }

    private PatternElement TransformPatternPart(CypherParser.OC_PatternPartContext ctx)
    {
        var element = TransformAnonymousPatternPart(ctx.oC_AnonymousPatternPart());
        if (ctx.oC_Variable() != null)
            element.SetPathName(TransformVariable(ctx.oC_Variable()));
        return element;
    }

    private PatternElement TransformAnonymousPatternPart(CypherParser.OC_AnonymousPatternPartContext ctx)
        => TransformPatternElement(ctx.oC_PatternElement());

    private List<PatternElement> TransformPathPatterns(CypherParser.OC_PathPatternsContext ctx)
    {
        var element = new PatternElement(TransformNodePattern(ctx.oC_NodePattern()));
        foreach (var chainCtx in ctx.oC_PatternElementChain())
            element.AddPatternElementChain(TransformPatternElementChain(chainCtx));
        return new List<PatternElement> { element };
    }

    private PatternElement RewriteBooleanPathPatternForCorrelation(
        PatternElement element,
        out ParsedExpression? correlationPredicate)
    {
        var renamedVariables = new List<(string Original, string Inner)>();
        var preservedRoot = false;

        NodePattern RewriteNode(NodePattern nodePattern)
        {
            var variableName = nodePattern.VariableName;
            if (!string.IsNullOrEmpty(variableName))
            {
                if (!preservedRoot)
                {
                    preservedRoot = true;
                }
                else
                {
                    var innerName = $"__g015_{variableName}_{renamedVariables.Count}";
                    renamedVariables.Add((variableName, innerName));
                    variableName = innerName;
                }
            }

            return new NodePattern(
                variableName,
                nodePattern.TableNames.ToList(),
                nodePattern.PropertyKeyValues.Select(kvp => (kvp.Key, kvp.Value.Copy())).ToList(),
                nodePattern.PropertyBagExpression?.Copy());
        }

        RelPattern RewriteRel(RelPattern relPattern)
            => new(
                relPattern.VariableName,
                relPattern.RelTypes.ToList(),
                relPattern.Direction,
                relPattern.PropertyKeyValues.Select(kvp => (kvp.Key, kvp.Value.Copy())).ToList(),
                relPattern.PropertyBagExpression?.Copy(),
                relPattern.LowerBound,
                relPattern.UpperBound,
                relPattern.RecursiveRelVariable,
                relPattern.RecursiveNodeVariable,
                relPattern.RecursiveFilter?.Copy());

        var rewritten = new PatternElement(RewriteNode(element.GetFirstNodePattern()));
        for (var chainIdx = 0; chainIdx < element.GetNumPatternElementChains(); chainIdx++)
        {
            var chain = element.GetPatternElementChain(chainIdx);
            rewritten.AddPatternElementChain(new PatternElementChain(
                RewriteRel(chain.RelPattern),
                RewriteNode(chain.NodePattern)));
        }

        if (element.HasPathName())
            rewritten.SetPathName(element.GetPathName());

        correlationPredicate = null;
        foreach (var (original, inner) in renamedVariables)
        {
            var equality = new StructuralParsedExpression(
                ExpressionType.EQUALS,
                new ParsedVariableExpression(inner, inner),
                new ParsedVariableExpression(original, original),
                $"{inner} = {original}");
            correlationPredicate = correlationPredicate == null
                ? equality
                : new StructuralParsedExpression(
                    ExpressionType.AND,
                    correlationPredicate,
                    equality,
                    $"{correlationPredicate.GetRawName()} AND {equality.GetRawName()}");
        }

        return rewritten;
    }

    private PatternElement TransformPatternElement(CypherParser.OC_PatternElementContext ctx)
    {
        if (ctx.oC_PatternElement() != null)
            return TransformPatternElement(ctx.oC_PatternElement());

        var element = new PatternElement(TransformNodePattern(ctx.oC_NodePattern()));
        foreach (var chainCtx in ctx.oC_PatternElementChain())
            element.AddPatternElementChain(TransformPatternElementChain(chainCtx));
        return element;
    }

    private NodePattern TransformNodePattern(CypherParser.OC_NodePatternContext ctx)
    {
        var variable = ctx.oC_Variable() != null ? TransformVariable(ctx.oC_Variable()) : string.Empty;
        var labels = new List<string>();
        if (ctx.oC_NodeLabels() != null)
        {
            // Correct ANTLR method: oC_LabelName() (not oC_NodeLabel())
            foreach (var labelCtx in ctx.oC_NodeLabels().oC_LabelName())
                labels.Add(TransformSchemaName(labelCtx.oC_SchemaName()));
        }
        var properties = TransformPropertyEntriesIfPresent(ctx.kU_Properties());
        var propertyBag = TransformPropertyBagIfPresent(ctx.kU_Properties());
        return new NodePattern(variable, labels, properties, propertyBag);
    }

    private PatternElementChain TransformPatternElementChain(CypherParser.OC_PatternElementChainContext ctx)
    {
        var rel = TransformRelationshipPattern(ctx.oC_RelationshipPattern());
        var node = TransformNodePattern(ctx.oC_NodePattern());
        return new PatternElementChain(rel, node);
    }

    private RelPattern TransformRelationshipPattern(CypherParser.OC_RelationshipPatternContext ctx)
    {
        var detail = ctx.oC_RelationshipDetail();
        var variable = string.Empty;
        var relTypes = new List<string>();
        var properties = new List<(string, ParsedExpression)>();
        ParsedExpression? propertyBag = null;
        string lowerBound = "1";
        string upperBound = "1";
        string? recRelVar = null;
        string? recNodeVar = null;
        ParsedExpression? recFilter = null;

        if (detail != null)
        {
            if (detail.oC_Variable() != null)
                variable = TransformVariable(detail.oC_Variable());
            if (detail.oC_RelationshipTypes() != null)
            {
                foreach (var rt in detail.oC_RelationshipTypes().oC_RelTypeName())
                    relTypes.Add(TransformSchemaName(rt.oC_SchemaName()));
            }
            properties = TransformPropertyEntriesIfPresent(detail.kU_Properties());
            propertyBag = TransformPropertyBagIfPresent(detail.kU_Properties());

            var recursiveDetail = detail.kU_RecursiveDetail();
            if (recursiveDetail != null)
            {
                var range = recursiveDetail.oC_RangeLiteral();
                if (range != null)
                {
                    if (range.oC_IntegerLiteral() != null)
                    {
                        lowerBound = range.oC_IntegerLiteral().GetText();
                        upperBound = range.oC_IntegerLiteral().GetText();
                    }
                    else
                    {
                        lowerBound = range.oC_LowerBound() != null ? range.oC_LowerBound().GetText() : "1";
                        upperBound = range.oC_UpperBound() != null ? range.oC_UpperBound().GetText() : ""; // empty signifies *
                    }
                }
                else
                {
                    // '*' provided without explicit limits implies 1..*
                    upperBound = "";
                }

                // Per-hop comprehension: (rr, nn | WHERE <predicate>). The grammar parses it here; capture
                // the intermediate variables and the optional WHERE so it can be evaluated during traversal.
                var comprehension = recursiveDetail.kU_RecursiveComprehension();
                if (comprehension != null)
                {
                    if (comprehension.oC_Variable(0) != null)
                        recRelVar = TransformVariable(comprehension.oC_Variable(0));
                    if (comprehension.oC_Variable(1) != null)
                        recNodeVar = TransformVariable(comprehension.oC_Variable(1));
                    if (comprehension.oC_Where() != null)
                        recFilter = TransformWhere(comprehension.oC_Where());
                }
            }
        }

        var direction = ctx.oC_LeftArrowHead() != null ? ArrowDirection.LEFT
                      : ctx.oC_RightArrowHead() != null ? ArrowDirection.RIGHT
                      : ArrowDirection.BOTH;

        return new RelPattern(variable, relTypes, direction, properties, propertyBag, lowerBound, upperBound,
            recRelVar, recNodeVar, recFilter);
    }

    private List<(string, ParsedExpression)> TransformPropertyEntriesIfPresent(CypherParser.KU_PropertiesContext? ctx)
    {
        var result = new List<(string, ParsedExpression)>();
        if (ctx == null) return result;
        if (ctx.oC_Parameter() != null) return result;
        for (var i = 0; i < ctx.oC_PropertyKeyName().Length; i++)
        {
            var key = TransformSchemaName(ctx.oC_PropertyKeyName(i).oC_SchemaName());
            var val = TransformExpression(ctx.oC_Expression(i));
            result.Add((key, val));
        }
        return result;
    }

    private ParsedExpression? TransformPropertyBagIfPresent(CypherParser.KU_PropertiesContext? ctx)
    {
        if (ctx?.oC_Parameter() == null)
            return null;
        return TransformParameter(ctx.oC_Parameter());
    }

    // ─── RETURN / WITH ─────────────────────────────────────────────────────────

    private ReturnClause TransformReturn(CypherParser.OC_ReturnContext ctx)
        => new ReturnClause(TransformProjectionBody(ctx.oC_ProjectionBody()));

    private ProjectionBody TransformProjectionBody(CypherParser.OC_ProjectionBodyContext ctx)
    {
        bool isDistinct = ctx.DISTINCT() != null;
        var items = TransformProjectionItems(ctx.oC_ProjectionItems());
        
        var orderByElements = new List<OrderByElement>();
        ParsedExpression? skip = null;
        ParsedExpression? limit = null;

        if (ctx.oC_Order() != null)
            orderByElements = TransformOrderBy(ctx.oC_Order());
        if (ctx.oC_Skip() != null)
            skip = TransformExpression(ctx.oC_Skip().oC_Expression());
        if (ctx.oC_Limit() != null)
            limit = TransformExpression(ctx.oC_Limit().oC_Expression());

        return new ProjectionBody(isDistinct, items, orderByElements, skip, limit);
    }

    private List<OrderByElement> TransformOrderBy(CypherParser.OC_OrderContext ctx)
    {
        var result = new List<OrderByElement>();
        foreach (var sortItem in ctx.oC_SortItem())
        {
            var expr = TransformExpression(sortItem.oC_Expression());
            bool isAscending = true;
            if (sortItem.DESCENDING() != null || sortItem.DESC() != null)
                isAscending = false;
            result.Add(new OrderByElement(expr, isAscending));
        }
        return result;
    }

    private List<ParsedExpression> TransformProjectionItems(CypherParser.OC_ProjectionItemsContext ctx)
    {
        var result = new List<ParsedExpression>();
        if (ctx.STAR() != null)
            result.Add(new ParsedStarExpression(ctx.STAR().GetText()));
        foreach (var itemCtx in ctx.oC_ProjectionItem())
            result.Add(TransformProjectionItem(itemCtx));
        return result;
    }

    private ParsedExpression TransformProjectionItem(CypherParser.OC_ProjectionItemContext ctx)
    {
        var expr = TransformExpression(ctx.oC_Expression());
        if (ctx.AS() != null)
            expr.SetAlias(TransformVariable(ctx.oC_Variable()));
        return expr;
    }

    // ─── Expressions ───────────────────────────────────────────────────────────

    public ParsedExpression TransformWhere(CypherParser.OC_WhereContext ctx)
        => TransformExpression(ctx.oC_Expression());

    public ParsedExpression TransformExpression(CypherParser.OC_ExpressionContext ctx)
        => TransformOrExpression(ctx.oC_OrExpression());

    private ParsedExpression TransformOrExpression(CypherParser.OC_OrExpressionContext ctx)
    {
        ParsedExpression? expr = null;
        foreach (var xorCtx in ctx.oC_XorExpression())
        {
            var next = TransformXorExpression(xorCtx);
            if (expr == null) { expr = next; continue; }
            expr = new StructuralParsedExpression(ExpressionType.OR, expr, next, expr.GetRawName() + " OR " + next.GetRawName());
        }
        return expr!;
    }

    private ParsedExpression TransformXorExpression(CypherParser.OC_XorExpressionContext ctx)
    {
        ParsedExpression? expr = null;
        foreach (var andCtx in ctx.oC_AndExpression())
        {
            var next = TransformAndExpression(andCtx);
            if (expr == null) { expr = next; continue; }
            expr = new StructuralParsedExpression(ExpressionType.XOR, expr, next, expr.GetRawName() + " XOR " + next.GetRawName());
        }
        return expr!;
    }

    private ParsedExpression TransformAndExpression(CypherParser.OC_AndExpressionContext ctx)
    {
        ParsedExpression? expr = null;
        foreach (var notCtx in ctx.oC_NotExpression())
        {
            var next = TransformNotExpression(notCtx);
            if (expr == null) { expr = next; continue; }
            expr = new StructuralParsedExpression(ExpressionType.AND, expr, next, expr.GetRawName() + " AND " + next.GetRawName());
        }
        return expr!;
    }

    private ParsedExpression TransformNotExpression(CypherParser.OC_NotExpressionContext ctx)
    {
        var result = TransformComparisonExpression(ctx.oC_ComparisonExpression());
        foreach (var _ in ctx.NOT())
        {
            result = new StructuralParsedExpression(ExpressionType.NOT, result, "NOT " + result.ToString());
        }
        return result;
    }

    private ParsedExpression TransformComparisonExpression(CypherParser.OC_ComparisonExpressionContext ctx)
    {
        if (ctx.kU_BitwiseOrOperatorExpression().Length == 1)
            return TransformBitwiseOrExpression(ctx.kU_BitwiseOrOperatorExpression(0));

        var left = TransformBitwiseOrExpression(ctx.kU_BitwiseOrOperatorExpression(0));
        var right = TransformBitwiseOrExpression(ctx.kU_BitwiseOrOperatorExpression(1));
        var op = ctx.kU_ComparisonOperator(0).GetText();
        var exprType = op switch
        {
            "=" => ExpressionType.EQUALS,
            "<>" => ExpressionType.NOT_EQUALS,
            ">" => ExpressionType.GREATER_THAN,
            ">=" => ExpressionType.GREATER_THAN_EQUALS,
            "<" => ExpressionType.LESS_THAN,
            _ => ExpressionType.LESS_THAN_EQUALS,
        };
        return new StructuralParsedExpression(exprType, left, right, ctx.GetText());
    }

    private ParsedExpression TransformBitwiseOrExpression(CypherParser.KU_BitwiseOrOperatorExpressionContext ctx)
    {
        ParsedExpression? expr = null;
        foreach (var andExpr in ctx.kU_BitwiseAndOperatorExpression())
        {
            var next = TransformBitwiseAndExpression(andExpr);
            if (expr == null) { expr = next; continue; }
            var f = new ParsedFunctionExpression("|", expr.GetRawName() + " | " + next.GetRawName());
            f.AddChild(expr); f.AddChild(next);
            expr = f;
        }
        return expr!;
    }

    private ParsedExpression TransformBitwiseAndExpression(CypherParser.KU_BitwiseAndOperatorExpressionContext ctx)
    {
        ParsedExpression? expr = null;
        foreach (var shiftExpr in ctx.kU_BitShiftOperatorExpression())
        {
            var next = TransformBitShiftExpression(shiftExpr);
            if (expr == null) { expr = next; continue; }
            var f = new ParsedFunctionExpression("&", expr.GetRawName() + " & " + next.GetRawName());
            f.AddChild(expr); f.AddChild(next);
            expr = f;
        }
        return expr!;
    }

    private ParsedExpression TransformBitShiftExpression(CypherParser.KU_BitShiftOperatorExpressionContext ctx)
    {
        ParsedExpression? expr = null;
        for (int i = 0; i < ctx.oC_AddOrSubtractExpression().Length; i++)
        {
            var next = TransformAddOrSubtract(ctx.oC_AddOrSubtractExpression(i));
            if (expr == null) { expr = next; continue; }
            var op = ctx.kU_BitShiftOperator(i - 1).GetText();
            var f = new ParsedFunctionExpression(op, expr.GetRawName() + " " + op + " " + next.GetRawName());
            f.AddChild(expr); f.AddChild(next);
            expr = f;
        }
        return expr!;
    }

    private ParsedExpression TransformAddOrSubtract(CypherParser.OC_AddOrSubtractExpressionContext ctx)
    {
        ParsedExpression? expr = null;
        for (int i = 0; i < ctx.oC_MultiplyDivideModuloExpression().Length; i++)
        {
            var next = TransformMultiplyDivide(ctx.oC_MultiplyDivideModuloExpression(i));
            if (expr == null) { expr = next; continue; }
            var op = ctx.kU_AddOrSubtractOperator(i - 1).GetText();
            var f = new ParsedFunctionExpression(op, expr.GetRawName() + " " + op + " " + next.GetRawName());
            f.AddChild(expr); f.AddChild(next);
            expr = f;
        }
        return expr!;
    }

    private ParsedExpression TransformMultiplyDivide(CypherParser.OC_MultiplyDivideModuloExpressionContext ctx)
    {
        ParsedExpression? expr = null;
        for (int i = 0; i < ctx.oC_PowerOfExpression().Length; i++)
        {
            var next = TransformPowerOf(ctx.oC_PowerOfExpression(i));
            if (expr == null) { expr = next; continue; }
            var op = ctx.kU_MultiplyDivideModuloOperator(i - 1).GetText();
            var f = new ParsedFunctionExpression(op, expr.GetRawName() + " " + op + " " + next.GetRawName());
            f.AddChild(expr); f.AddChild(next);
            expr = f;
        }
        return expr!;
    }

    private ParsedExpression TransformPowerOf(CypherParser.OC_PowerOfExpressionContext ctx)
    {
        ParsedExpression? expr = null;
        foreach (var slnCtx in ctx.oC_StringListNullOperatorExpression())
        {
            var next = TransformStringListNullOp(slnCtx);
            if (expr == null) { expr = next; continue; }
            var f = new ParsedFunctionExpression("^", expr.GetRawName() + " ^ " + next.GetRawName());
            f.AddChild(expr); f.AddChild(next);
            expr = f;
        }
        return expr!;
    }

    private ParsedExpression TransformStringListNullOp(CypherParser.OC_StringListNullOperatorExpressionContext ctx)
    {
        var base_ = TransformUnaryAddSubtract(ctx.oC_UnaryAddSubtractOrFactorialExpression());

        if (ctx.oC_NullOperatorExpression() != null)
        {
            var nullCtx = ctx.oC_NullOperatorExpression();
            var raw = base_.GetRawName() + " " + nullCtx.GetText();
            var type = nullCtx.NOT() != null ? ExpressionType.IS_NOT_NULL : ExpressionType.IS_NULL;
            return new StructuralParsedExpression(type, base_, raw);
        }

        // ── IN operator: expr IN listExpr → list_contains(listExpr, expr) ──
        if (ctx.oC_ListOperatorExpression() != null && ctx.oC_ListOperatorExpression().Length > 0)
        {
            var listCtx = ctx.oC_ListOperatorExpression(0);
            if (listCtx.IN() != null && listCtx.oC_PropertyOrLabelsExpression() != null)
            {
                var listExpr = TransformPropertyOrLabels(listCtx.oC_PropertyOrLabelsExpression());
                var raw = base_.GetRawName() + " IN " + listExpr.GetRawName();
                var func = new ParsedFunctionExpression("list_contains", raw);
                func.AddChild(listExpr);
                func.AddChild(base_);
                return func;
            }
        }

        // ── String operators: STARTS WITH, ENDS WITH, CONTAINS ─────────────
        if (ctx.oC_StringOperatorExpression() != null)
        {
            var strCtx = ctx.oC_StringOperatorExpression();
            var operand = TransformPropertyOrLabels(strCtx.oC_PropertyOrLabelsExpression());

            string funcName;
            if (strCtx.STARTS() != null)
                funcName = "starts_with";
            else if (strCtx.ENDS() != null)
                funcName = "ends_with";
            else if (strCtx.CONTAINS() != null)
                funcName = "contains";
            else
            {
                // Regular expression operator — not yet supported
                return base_;
            }

            var raw = base_.GetRawName() + " " + strCtx.GetText().Trim();
            var func = new ParsedFunctionExpression(funcName, raw);
            func.AddChild(base_);
            func.AddChild(operand);
            return func;
        }

        return base_;
    }

    private ParsedExpression TransformUnaryAddSubtract(CypherParser.OC_UnaryAddSubtractOrFactorialExpressionContext ctx)
    {
        var result = TransformPropertyOrLabels(ctx.oC_PropertyOrLabelsExpression());
        if (ctx.MINUS().Length > 0)
        {
            var f = new ParsedFunctionExpression("-", "-" + result.ToString());
            f.AddChild(result);
            result = f;
        }
        return result;
    }

    private ParsedExpression TransformPropertyOrLabels(CypherParser.OC_PropertyOrLabelsExpressionContext ctx)
    {
        var atom = TransformAtom(ctx.oC_Atom());
        foreach (var lookup in ctx.oC_PropertyLookup())
        {
            var propName = lookup.STAR() != null
                ? "*"
                : TransformSchemaName(lookup.oC_PropertyKeyName().oC_SchemaName());
            var raw = atom.ToString() + "." + propName;
            atom = new ParsedPropertyExpression(propName, atom, raw);
        }
        return atom;
    }

    private ParsedExpression TransformAtom(CypherParser.OC_AtomContext ctx)
    {
        if (ctx.oC_Literal() != null)
            return TransformLiteral(ctx.oC_Literal());
        if (ctx.oC_ParenthesizedExpression() != null)
            return TransformExpression(ctx.oC_ParenthesizedExpression().oC_Expression());
        if (ctx.oC_FunctionInvocation() != null)
            return TransformFunctionInvocation(ctx.oC_FunctionInvocation());
        if (ctx.oC_PathPatterns() != null)
            return TransformPathPatternAtom(ctx.oC_PathPatterns());
        if (ctx.oC_ExistCountSubquery() != null)
            return TransformExistCountSubquery(ctx.oC_ExistCountSubquery());
        if (ctx.oC_CaseExpression() != null)
            return TransformCaseExpression(ctx.oC_CaseExpression());
        if (ctx.oC_Parameter() != null)
            return TransformParameter(ctx.oC_Parameter());
        if (ctx.oC_Quantifier() != null)
            return TransformQuantifier(ctx.oC_Quantifier());
        if (ctx.oC_Variable() != null)
            return new ParsedVariableExpression(TransformVariable(ctx.oC_Variable()), ctx.GetText());

        throw new InvalidOperationException($"Internal error: unreachable atom branch: {ctx.GetText()}");
    }

    private ParsedExpression TransformPathPatternAtom(CypherParser.OC_PathPatternsContext ctx)
    {
        var rewrittenPatterns = new List<PatternElement>();
        ParsedExpression? combinedCorrelationPredicate = null;
        foreach (var pattern in TransformPathPatterns(ctx))
        {
            var rewritten = RewriteBooleanPathPatternForCorrelation(pattern, out var correlationPredicate);
            rewrittenPatterns.Add(rewritten);
            if (correlationPredicate == null)
                continue;

            combinedCorrelationPredicate = combinedCorrelationPredicate == null
                ? correlationPredicate
                : new StructuralParsedExpression(
                    ExpressionType.AND,
                    combinedCorrelationPredicate,
                    correlationPredicate,
                    $"{combinedCorrelationPredicate.GetRawName()} AND {correlationPredicate.GetRawName()}");
        }

        var matchClause = new MatchClause(rewrittenPatterns, ClauseType.MATCH);
        if (combinedCorrelationPredicate != null)
            matchClause.SetWherePredicate(combinedCorrelationPredicate);
        var singleQuery = new SingleQuery();
        singleQuery.AddReadingClause(matchClause);
        var regularQuery = new RegularQuery(singleQuery);
        return new ParsedSubqueryExpression(SubqueryType.EXISTS, regularQuery, ctx.GetText());
    }

    private ParsedExpression TransformParameter(CypherParser.OC_ParameterContext ctx)
    {
        var parameterName = ctx.oC_SymbolicName() != null
            ? TransformSymbolicName(ctx.oC_SymbolicName())
            : ctx.DecimalInteger().GetText();
        return new ParsedParameterExpression(parameterName, ctx.GetText());
    }

    private ParsedExpression TransformQuantifier(CypherParser.OC_QuantifierContext ctx)
    {
        var filter = ctx.oC_FilterExpression();
        var idInCollection = filter.oC_IdInColl();
        var variableName = TransformVariable(idInCollection.oC_Variable());
        var collectionExpression = TransformExpression(idInCollection.oC_Expression());
        var predicateExpression = TransformWhere(filter.oC_Where());
        var quantifierName = ctx.ALL() != null ? "ALL"
            : ctx.ANY() != null ? "ANY"
            : ctx.NONE() != null ? "NONE"
            : "SINGLE";

        return new ParsedQuantifierExpression(
            quantifierName,
            variableName,
            collectionExpression,
            predicateExpression,
            ctx.GetText());
    }

    private ParsedExpression TransformCaseExpression(CypherParser.OC_CaseExpressionContext ctx)
    {
        var alternatives = ctx.oC_CaseAlternative();
        var hasElse = ctx.ELSE() != null;
        var rootExpressions = ctx.oC_Expression();
        var searchedCaseExpressionCount = alternatives.Length * 2 + (hasElse ? 1 : 0);
        var isSimpleCase = rootExpressions.Length == searchedCaseExpressionCount + 1;

        var expr = new ParsedFunctionExpression(isSimpleCase ? "CASE_SIMPLE" : "CASE", ctx.GetText());
        if (isSimpleCase)
            expr.AddChild(TransformExpression(rootExpressions[0]));

        foreach (var alternative in alternatives)
        {
            expr.AddChild(TransformExpression(alternative.oC_Expression(0)));
            expr.AddChild(TransformExpression(alternative.oC_Expression(1)));
        }

        if (hasElse)
            expr.AddChild(TransformExpression(rootExpressions[^1]));

        return expr;
    }

    private ParsedExpression TransformLiteral(CypherParser.OC_LiteralContext ctx)
    {
        if (ctx.oC_NumberLiteral() != null)
            return TransformNumberLiteral(ctx.oC_NumberLiteral());
        if (ctx.oC_BooleanLiteral() != null)
        {
            bool val = ctx.oC_BooleanLiteral().TRUE() != null;
            return new ParsedLiteralExpression(val, ctx.GetText());
        }
        if (ctx.StringLiteral() != null)
            return new ParsedLiteralExpression(TransformStringLiteral(ctx.StringLiteral()), ctx.GetText());
        if (ctx.NULL() != null)  // Correct token: NULL() not NULL_()
            return new ParsedLiteralExpression(ctx.GetText());

        if (ctx.oC_ListLiteral() != null)
            return TransformListLiteral(ctx.oC_ListLiteral());
        if (ctx.kU_StructLiteral() != null)
            return TransformStructLiteral(ctx.kU_StructLiteral());

        throw new InvalidOperationException($"Internal error: unreachable literal branch: {ctx.GetText()}");
    }

    private ParsedExpression TransformListLiteral(CypherParser.OC_ListLiteralContext ctx)
    {
        // The grammar for oC_listLiteral is:
        //   '[' SP? oC_Expression? SP? (kU_ListEntry SP?)* ']'
        // where kU_ListEntry = ',' SP? oC_Expression?
        // So the first element (if any) is ctx.oC_Expression(),
        // and subsequent elements are ctx.kU_ListEntry(i).oC_Expression().
        var listFunc = new ParsedFunctionExpression("LIST_LITERAL", ctx.GetText());

        // First element
        if (ctx.oC_Expression() != null)
            listFunc.AddChild(TransformExpression(ctx.oC_Expression()));

        // Additional comma-separated elements
        foreach (var entry in ctx.kU_ListEntry())
        {
            if (entry.oC_Expression() != null)
                listFunc.AddChild(TransformExpression(entry.oC_Expression()));
        }

        return listFunc;
    }

    private ParsedExpression TransformStructLiteral(CypherParser.KU_StructLiteralContext ctx)
    {
        var structExpr = new ParsedFunctionExpression("STRUCT_LITERAL", ctx.GetText());
        foreach (var field in ctx.kU_StructField())
        {
            string key;
            if (field.oC_SymbolicName() != null)
            {
                key = field.oC_SymbolicName().GetText();
            }
            else
            {
                key = TransformStringLiteral(field.StringLiteral());
            }

            var valueExpr = TransformExpression(field.oC_Expression());
            structExpr.AddChild(new ParsedLiteralExpression(key, key, LogicalTypeID.STRING));
            structExpr.AddChild(valueExpr);
        }

        return structExpr;
    }


    private ParsedExpression TransformNumberLiteral(CypherParser.OC_NumberLiteralContext ctx)
    {
        if (ctx.oC_IntegerLiteral() != null)
        {
            var text = ctx.oC_IntegerLiteral().DecimalInteger().GetText();
            if (long.TryParse(text, out var longVal))
                return new ParsedLiteralExpression(longVal, ctx.GetText());
            return new ParsedLiteralExpression(0L, ctx.GetText());
        }
        var dText = ctx.oC_DoubleLiteral().GetText();
        if (double.TryParse(dText, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var dVal))
            return new ParsedLiteralExpression(dVal, ctx.GetText());
        return new ParsedLiteralExpression(0.0, ctx.GetText());
    }

    public ParsedFunctionExpression TransformFunctionInvocation(CypherParser.OC_FunctionInvocationContext ctx)
    {
        if (ctx.STAR() != null)
            return new ParsedFunctionExpression("COUNT_STAR", ctx.GetText());

        string funcName;
        if (ctx.COUNT() != null)
            funcName = "COUNT";
        else if (ctx.CAST() != null)
            funcName = "CAST";
        else
            funcName = TransformFunctionName(ctx.oC_FunctionName());

        var expr = new ParsedFunctionExpression(funcName, ctx.GetText(), ctx.DISTINCT() != null);
        foreach (var paramCtx in ctx.kU_FunctionParameter())
        {
            if (paramCtx.kU_LambdaParameter() != null)
            {
                var lambdaCtx = paramCtx.kU_LambdaParameter();
                var vars = new List<string>();
                foreach (var sym in lambdaCtx.kU_LambdaVars().oC_SymbolicName())
                    vars.Add(TransformSymbolicName(sym));
                var body = TransformExpression(lambdaCtx.oC_Expression());
                expr.AddChild(new ParsedLambdaExpression(vars, body, paramCtx.GetText()));
            }
            else
            {
                var paramExpr = TransformExpression(paramCtx.oC_Expression());
                if (paramCtx.oC_SymbolicName() != null)
                    paramExpr.SetAlias(TransformSymbolicName(paramCtx.oC_SymbolicName()));
                expr.AddChild(paramExpr);
            }
        }

        // CAST(expr AS TYPE) carries the target type outside kU_FunctionParameter().
        // Preserve it as the second argument expected by the function dispatcher.
        if (ctx.CAST() != null && ctx.kU_DataType() != null)
        {
            var targetType = ctx.kU_DataType().GetText();
            expr.AddChild(new ParsedLiteralExpression(targetType, targetType));
        }

        return expr;
    }

    private ParsedExpression TransformExistCountSubquery(CypherParser.OC_ExistCountSubqueryContext ctx)
    {
        var type = ctx.EXISTS() != null ? SubqueryType.EXISTS : SubqueryType.COUNT;
        var matchClause = new MatchClause(TransformPattern(ctx.oC_Pattern()), ClauseType.MATCH);
        if (ctx.oC_Where() != null)
            matchClause.SetWherePredicate(TransformWhere(ctx.oC_Where()));

        var singleQuery = new SingleQuery();
        singleQuery.AddReadingClause(matchClause);
        var regularQuery = new RegularQuery(singleQuery);

        return new ParsedSubqueryExpression(type, regularQuery, ctx.GetText());
    }

    // ─── Helpers ───────────────────────────────────────────────────────────────

    private string TransformFunctionName(CypherParser.OC_FunctionNameContext ctx)
        => TransformSymbolicName(ctx.oC_SymbolicName());

    private string TransformVariable(CypherParser.OC_VariableContext ctx)
        => TransformSymbolicName(ctx.oC_SymbolicName());

    private string TransformSchemaName(CypherParser.OC_SchemaNameContext ctx)
        => TransformSymbolicName(ctx.oC_SymbolicName());

    public static string TransformSymbolicName(CypherParser.OC_SymbolicNameContext ctx)
    {
        if (ctx.EscapedSymbolicName() != null)
        {
            var escaped = ctx.EscapedSymbolicName().GetText();
            return escaped.Substring(1, escaped.Length - 2);
        }
        return ctx.GetText();
    }

    private static string TransformStringLiteral(ITerminalNode node)
    {
        var str = node.GetText();
        var content = str.Substring(1, str.Length - 2);
        return content.Replace("\\'", "'").Replace("\\\"", "\"").Replace("\\\\", "\\")
                      .Replace("\\n", "\n").Replace("\\t", "\t").Replace("\\r", "\r");
    }

    // ─── COPY FROM ─────────────────────────────────────────────────────────────

    public Statement TransformCopyFrom(CypherParser.KU_CopyFromContext ctx)
    {
        var tableName = TransformSymbolicName(ctx.oC_SchemaName().oC_SymbolicName());
        var filePath = TransformStringLiteral(ctx.kU_ScanSource().kU_FilePaths().StringLiteral()[0]);
        return new CopyFrom(tableName, filePath);
    }

    public Statement TransformCopyTo(CypherParser.KU_CopyTOContext ctx)
    {
        var query = (RegularQuery)TransformQuery(ctx.oC_Query());
        var filePath = TransformStringLiteral(ctx.StringLiteral());
        return new CopyTo(query, filePath);
    }
    // ─── Extended Statement Transforms ──────────────────────────────────────────

    private Statement TransformCreateSequence(CypherParser.KU_CreateSequenceContext ctx)
    {
        var ifNotExists = ctx.kU_IfNotExists() != null;
        var seqName = TransformSchemaName(ctx.oC_SchemaName());
        long incrementBy = 1;
        long? minValue = null, maxValue = null, startWith = null;
        bool cycle = false;

        if (ctx.kU_SequenceOptions() != null)
        {
            foreach (var opt in ctx.kU_SequenceOptions())
            {
                if (opt.kU_IncrementBy() != null)
                {
                    var intText = opt.kU_IncrementBy().oC_IntegerLiteral()?.GetText() ?? "1";
                    incrementBy = long.Parse(intText);
                    if (opt.kU_IncrementBy().MINUS() != null)
                        incrementBy = -incrementBy;
                }
                else if (opt.kU_MinValue() != null)
                {
                    if (opt.kU_MinValue().NO() == null)
                    {
                        var intText = opt.kU_MinValue().oC_IntegerLiteral()?.GetText() ?? "0";
                        minValue = long.Parse(intText);
                        if (opt.kU_MinValue().MINUS() != null)
                            minValue = -minValue;
                    }
                }
                else if (opt.kU_MaxValue() != null)
                {
                    if (opt.kU_MaxValue().NO() == null)
                    {
                        var intText = opt.kU_MaxValue().oC_IntegerLiteral()?.GetText() ?? "0";
                        maxValue = long.Parse(intText);
                        if (opt.kU_MaxValue().MINUS() != null)
                            maxValue = -maxValue;
                    }
                }
                else if (opt.kU_StartWith() != null)
                {
                    var intText = opt.kU_StartWith().oC_IntegerLiteral()?.GetText() ?? "1";
                    startWith = long.Parse(intText);
                    if (opt.kU_StartWith().MINUS() != null)
                        startWith = -startWith;
                }
                else if (opt.kU_Cycle() != null)
                {
                    cycle = opt.kU_Cycle().NO() == null;
                }
            }
        }

        return new CreateSequenceStatement(seqName, ifNotExists, incrementBy, minValue, maxValue, startWith, cycle);
    }

    private Statement TransformCreateType(CypherParser.KU_CreateTypeContext ctx)
    {
        var typeName = TransformSchemaName(ctx.oC_SchemaName());
        var dataType = ctx.kU_DataType()?.GetText() ?? "";
        return new CreateTypeStatement(typeName, dataType);
    }

    private Statement TransformCommentOn(CypherParser.KU_CommentOnContext ctx)
    {
        var tableName = TransformSchemaName(ctx.oC_SchemaName());
        var comment = TransformStringLiteral(ctx.StringLiteral());
        return new CommentOnStatement(tableName, comment);
    }

    private Statement TransformExportDatabase(CypherParser.KU_ExportDatabaseContext ctx)
    {
        var exportPath = TransformStringLiteral(ctx.StringLiteral());
        var options = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        if (ctx.kU_Options() != null)
        {
            foreach (var optionCtx in ctx.kU_Options().kU_Option())
            {
                var optionName = optionCtx.oC_SymbolicName()?.GetText() ?? string.Empty;
                if (string.IsNullOrWhiteSpace(optionName))
                    continue;

                if (optionCtx.oC_Literal() != null)
                {
                    var literal = TransformLiteral(optionCtx.oC_Literal()) as ParsedLiteralExpression;
                    options[optionName] = literal?.Value;
                }
                else
                {
                    options[optionName] = true;
                }
            }
        }
        return new ExportDatabaseStatement(exportPath, options);
    }

    private Statement TransformImportDatabase(CypherParser.KU_ImportDatabaseContext ctx)
    {
        var importPath = TransformStringLiteral(ctx.StringLiteral());
        return new ImportDatabaseStatement(importPath);
    }

    public Statement TransformTransaction(CypherParser.KU_TransactionContext ctx)
    {
        if (ctx.BEGIN() != null)
        {
            var isReadOnly = ctx.READ() != null && ctx.ONLY() != null;
            return new TransactionStatement(TransactionCommand.BEGIN, isReadOnly);
        }
        if (ctx.COMMIT() != null)
            return new TransactionStatement(TransactionCommand.COMMIT);
        if (ctx.ROLLBACK() != null)
            return new TransactionStatement(TransactionCommand.ROLLBACK);
        if (ctx.CHECKPOINT() != null)
            return new TransactionStatement(TransactionCommand.CHECKPOINT);

        throw new InvalidOperationException($"Internal error: unreachable transaction branch: {ctx.GetText()}");
    }
}

/// <summary>Parsed STAR projection (*) expression.</summary>
public sealed class ParsedStarExpression : ParsedExpression
{
    public ParsedStarExpression(string rawName) : base(ExpressionType.STAR, rawName) { }
    public override ParsedExpression Copy() => new ParsedStarExpression(_rawName);
}
