//! GXProcedure C# class generator.

mod fields;

use crate::{
    catalog::{KbObject, ObjectKind},
    csharp::{
        calls::{CallParam, CallSignatures, CallTarget},
        emitter::CSharpEmitter,
        sdt::SdtIndex,
        types::{
            clean_ident, to_csharp_default_value, to_csharp_param_name, to_csharp_param_signature,
            to_csharp_type, to_csharp_var_name,
        },
    },
    frontend::{Procedure, Stmt},
    model::{DataType, ParamMode, ProcedureSource, parse_parameter},
};
use std::collections::BTreeMap;
use std::fmt::Write;
use std::sync::Arc;

/// The value an out parameter holds on entry to `execute` and `executeSubmit`, before the body runs.
///
/// The SDK gives a scalar its type's zero value (`this.AV8Value = 0 ;`, `""`, `DateTime.MinValue`) and an
/// SDT a new object (`this.AV8Value = new SdtRustFreshSdt091817(context) ;`, rphout68979100.cs). A null
/// SDT would throw when the body reads a member before writing it.
fn out_parameter_default(data_type: &DataType) -> String {
    if matches!(data_type, DataType::Numeric { .. }) {
        // The SDK writes `0` for every numeric, decimal included (`this.AV9Result = 0 ;`, dch37961700.cs).
        return "0".to_string();
    }
    allocation_expression(data_type).unwrap_or_else(|| to_csharp_default_value(data_type))
}

/// SDK allocations for records and root SDT collections (`listprograms.cs`).
pub(super) fn allocation_expression(data_type: &DataType) -> Option<String> {
    match data_type {
        DataType::Record { .. } => Some(format!("new {}(context)", to_csharp_type(data_type))),
        DataType::BaseCollection {
            item, namespace, ..
        } => Some(format!(
            "new {}( context, \"{item}\", \"{namespace}\")",
            to_csharp_type(data_type)
        )),
        _ => None,
    }
}

/// Standard imports matching official GeneXus IDE generated code.
pub const STANDARD_IMPORTS: &[&str] = &[
    "System",
    "System.Collections",
    "GeneXus.Utils",
    "GeneXus.Resources",
    "GeneXus.Application",
    "GeneXus.Metadata",
    "GeneXus.Cryptography",
    "com.genexus",
    "GeneXus.Data.ADO",
    "GeneXus.Data.NTier",
    "GeneXus.Data.NTier.ADO",
    "GeneXus.WebControls",
    "GeneXus.Http",
    "GeneXus.Procedure",
    "GeneXus.XML",
    "GeneXus.Search",
    "GeneXus.Encryption",
    "GeneXus.Http.Client",
    "System.Threading",
    "System.Xml.Serialization",
    "System.Runtime.Serialization",
];

/// Typed parameter definition for C# signature generation.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct CsharpParam {
    pub name: String,
    pub data_type: DataType,
    pub mode: ParamMode,
    pub param_name: String,
    pub var_name: String,
}

/// A Procedure's checked emission: its C# or its named refusals, and the upper-cased class names of the
/// Procedures its call sites name.
#[derive(Debug, Clone, PartialEq)]
pub struct CheckedEmit {
    pub code: Result<String, String>,
    pub callees: std::collections::BTreeSet<String>,
}

/// Represents an emittable GeneXus C# Procedure class (`GXProcedure`).
#[derive(Debug, Clone)]
pub struct ProcedureClass {
    pub name: String,
    pub namespace: String,
    pub theme: Option<String>,
    pub is_main: bool,
    pub params: Vec<CsharpParam>,
    pub variables: BTreeMap<String, DataType>,
    pub return_type: Option<DataType>,
    pub body: Vec<Stmt>,
    pub subroutines: Vec<(String, Vec<Stmt>)>,
    /// The SDTs this Procedure's member accesses resolve against. Empty until set with
    /// [`ProcedureClass::with_sdts`]; a member access against an empty index does not resolve.
    pub sdts: Arc<SdtIndex>,
    /// The Procedures this build emits, for call sites. `None` leaves calls as they were; a build sets it
    /// with [`ProcedureClass::with_calls`].
    pub calls: Option<Arc<CallSignatures>>,
    /// Namespaces imported after the standard ones: the module namespaces of the build's SDTs, so a
    /// type of an SDT another module holds resolves by its class name.
    pub usings: Vec<String>,
    /// The stored id of each variable, by name (the reader's `VARIABLE_IDS` part). With ids the class is
    /// written as the SDK writes it: fields `AV<Id><Name>`, declared in id order, and only those the code
    /// uses. Without them (portable sources) variables are `AV_<Name>`, all declared.
    pub var_ids: BTreeMap<String, u32>,
    /// Navigation levels for database queries and cursors.
    pub navigation_levels: Vec<crate::navigation::NavigationLevel>,
}

impl ProcedureClass {
    /// Creates a new empty ProcedureClass with default namespace and settings.
    pub fn new(name: impl Into<String>) -> Self {
        Self {
            name: name.into(),
            namespace: "GeneXus.Programs".to_string(),
            usings: Vec::new(),
            theme: Some("KBTeste".to_string()),
            is_main: false,
            params: Vec::new(),
            variables: BTreeMap::new(),
            return_type: None,
            body: Vec::new(),
            subroutines: Vec::new(),
            sdts: Arc::new(SdtIndex::default()),
            calls: None,
            var_ids: BTreeMap::new(),
            navigation_levels: Vec::new(),
        }
    }

    /// Sets the navigation levels for this Procedure.
    pub fn with_navigation_levels(
        mut self,
        levels: Vec<crate::navigation::NavigationLevel>,
    ) -> Self {
        self.navigation_levels = levels;
        self
    }

    /// Sets the stored id of each variable, renaming the parameter fields to the SDK's `AV<Id><Name>`.
    pub fn with_var_ids(mut self, ids: BTreeMap<String, u32>) -> Self {
        self.var_ids = ids;
        let names: Vec<String> = self.params.iter().map(|p| self.cs_var(&p.name)).collect();
        for (param, name) in self.params.iter_mut().zip(names) {
            param.var_name = name;
        }
        self
    }

    /// The C# field of a variable (see [`ProcedureClass::var_ids`]).
    pub fn cs_var(&self, name: &str) -> String {
        crate::csharp::emitter::sdk_var_name(
            (!self.var_ids.is_empty()).then_some(&self.var_ids),
            name,
        )
    }

    /// Whether this class is written with the SDK's variable names (its stored ids are known).
    fn sdk_names(&self) -> bool {
        !self.var_ids.is_empty()
    }

    /// Sets the SDTs this Procedure's member accesses resolve against.
    pub fn with_sdts(mut self, sdts: Arc<SdtIndex>) -> Self {
        self.sdts = sdts;
        self
    }

    /// Sets the Procedures this build emits, so that each call site is written in the SDK's form or refused.
    pub fn with_calls(mut self, calls: Arc<CallSignatures>) -> Self {
        self.calls = Some(calls);
        self
    }

    /// The class name the SDK gives a Procedure: its name in lower case (`rusttimingprobe`). Callers and
    /// the runtime find a class by this name, so it is the name a call site records.
    pub fn class_name(&self) -> String {
        self.name.to_ascii_lowercase()
    }

    /// A Procedure without parameters is a main program. The SDK emits its body and `Main` in an
    /// `a`-prefixed class, and a wrapper under the plain name that starts it through `ClassLoader`
    /// (`arusttimingprobe.cs` and `rusttimingprobe.cs` in the KBTeste NetModel). A Procedure with
    /// parameters is one class (`execute` with the parameters, `executeUdp`), with no `Main`.
    pub fn is_split(&self) -> bool {
        self.params.is_empty()
    }

    /// The class this Procedure's main file declares: `a<name>` for a split main program, `<name>`
    /// otherwise.
    pub fn emitted_class_name(&self) -> String {
        if self.is_split() {
            format!("a{}", self.class_name())
        } else {
            self.class_name()
        }
    }

    /// The parameters of this Procedure as its callers see them.
    pub fn call_target(&self) -> CallTarget {
        CallTarget {
            class_name: self.class_name(),
            namespace: self.namespace.clone(),
            params: self
                .params
                .iter()
                .map(|p| CallParam {
                    mode: p.mode,
                    data_type: p.data_type.clone(),
                })
                .collect(),
        }
    }

    /// Sets the application theme for GxContext.
    pub fn with_theme(mut self, theme: impl Into<String>) -> Self {
        self.theme = Some(theme.into());
        self
    }

    /// Sets whether this procedure is an entry point / main executable.
    pub fn with_is_main(mut self, is_main: bool) -> Self {
        self.is_main = is_main;
        self
    }

    /// Sets custom namespace.
    /// Imports the module namespaces of the build's SDTs (its own namespace is never repeated).
    pub fn with_usings(mut self, usings: Vec<String>) -> Self {
        self.usings = usings;
        self
    }

    pub fn with_namespace(mut self, namespace: impl Into<String>) -> Self {
        self.namespace = namespace.into();
        self
    }

    /// Adds a typed parameter.
    pub fn add_param(mut self, param: CsharpParam) -> Self {
        self.params.push(param);
        self
    }

    /// Adds a declared variable.
    pub fn add_variable(mut self, name: impl Into<String>, data_type: DataType) -> Self {
        self.variables.insert(name.into(), data_type);
        self
    }

    /// Constructs a ProcedureClass from parsed AST and variable definitions.
    pub fn from_ast(
        name: &str,
        procedure: &Procedure,
        variables: &BTreeMap<String, DataType>,
    ) -> Self {
        let mut main_body = Vec::new();
        let mut subroutines = Vec::new();

        for stmt in &procedure.body {
            if let Stmt::Sub(sub_name, sub_body) = stmt {
                subroutines.push((sub_name.clone(), sub_body.clone()));
            } else {
                main_body.push(stmt.clone());
            }
        }

        let mut params = Vec::with_capacity(procedure.params.len());
        for (i, p) in procedure.params.iter().enumerate() {
            let (mode, param_ident) = parse_parameter(p).unwrap_or((ParamMode::InOut, p.as_str()));
            let lookup_key = param_ident.trim_start_matches('&');
            let fallback_name = clean_ident(param_ident);
            let (matched_name, data_type) = variables
                .iter()
                .find(|(k, _)| {
                    k.eq_ignore_ascii_case(lookup_key) || k.eq_ignore_ascii_case(param_ident)
                })
                .map(|(k, v)| (k.clone(), v.clone()))
                .unwrap_or_else(|| {
                    (
                        fallback_name,
                        DataType::Numeric {
                            precision: 10,
                            scale: 0,
                        },
                    )
                });

            let clean = clean_ident(&matched_name);
            let param_name = to_csharp_param_name(i, &matched_name);
            let var_name = to_csharp_var_name(&matched_name);
            params.push(CsharpParam {
                name: clean,
                data_type,
                mode,
                param_name,
                var_name,
            });
        }

        Self {
            name: name.to_string(),
            namespace: "GeneXus.Programs".to_string(),
            usings: Vec::new(),
            theme: Some("KBTeste".to_string()),
            is_main: false,
            params,
            variables: variables.clone(),
            return_type: None,
            body: main_body,
            subroutines,
            sdts: Arc::new(SdtIndex::default()),
            calls: None,
            var_ids: BTreeMap::new(),
            navigation_levels: Vec::new(),
        }
    }

    /// Constructs a ProcedureClass from a `ProcedureSource` definition.
    pub fn from_source(source: &ProcedureSource) -> Result<Self, String> {
        let body = crate::frontend::parse_body(&source.source)
            .map_err(|e| format!("procedure {}: {e}", source.name))?;
        let ast = Procedure {
            name: source.name.clone(),
            params: source.params.clone(),
            body,
        };
        let mut proc = Self::from_ast(&source.name, &ast, &source.variables);
        proc.return_type = source.return_type.clone();
        Ok(proc)
    }

    /// Constructs a ProcedureClass directly from a portable `KbObject`.
    /// The class is declared in the namespace of the Modules that hold the object.
    pub fn from_kb_object(object: &KbObject) -> Result<Self, String> {
        Self::from_kb_object_at_root(object).map(|class| {
            class.with_namespace(crate::csharp::types::module_namespace(&object.module_path))
        })
    }

    fn from_kb_object_at_root(object: &KbObject) -> Result<Self, String> {
        if object.kind == ObjectKind::DataProvider {
            return super::data_provider::procedure_class(object);
        }
        if object.kind != ObjectKind::Procedure {
            return Err(format!("expected Procedure, found {:?}", object.kind));
        }

        let mut source_code = "";
        let mut var_ids: BTreeMap<String, u32> = BTreeMap::new();
        let mut variables: BTreeMap<String, DataType> = BTreeMap::new();
        let mut params: Vec<String> = Vec::new();

        for part in &object.source_parts {
            match part.name.as_str() {
                "SOURCE" => source_code = &part.source,
                crate::udm::reader::VARIABLE_IDS_PART => {
                    var_ids = serde_json::from_str(&part.source).map_err(|e| {
                        format!("variable ids of '{}' are not readable: {e}", object.name)
                    })?;
                }
                "VARIABLES" => {
                    // An empty part declares no variables. Any other text must be the portable
                    // form: a part this engine cannot read is an error, never a silent loss of
                    // declarations (the generated class would reference names it does not declare).
                    if !part.source.trim().is_empty() {
                        variables = serde_json::from_str::<BTreeMap<String, DataType>>(&part.source)
                            .map_err(|e| {
                                format!(
                                    "variables of '{}' are not in the portable form this engine reads: {e}",
                                    object.name
                                )
                            })?;
                    }
                }
                "RULES" => {
                    // A rules part that does not parse is an error: the parameters it declares
                    // would be lost, and the generated class would reference them undeclared.
                    let rules = crate::rules::parse_rules(&part.source)
                        .map_err(|e| format!("rules of '{}': {e}", object.name))?;
                    for rule in rules {
                        if let crate::rules::Rule::Parm(args) = rule {
                            for arg in args {
                                if arg.mode.is_empty() {
                                    params.push(arg.name);
                                } else {
                                    params.push(format!("{}:{}", arg.mode, arg.name));
                                }
                            }
                        }
                    }
                }
                _ => {}
            }
        }

        let body = if source_code.is_empty() {
            Vec::new()
        } else if var_ids.is_empty() {
            crate::frontend::parse_body(source_code)
                .map_err(|e| format!("object {}: {e}", object.name))?
        } else {
            // The SDK's form keeps the source's parentheses.
            crate::frontend::keep_parentheses(|| crate::frontend::parse_body(source_code))
                .map_err(|e| format!("object {}: {e}", object.name))?
        };

        let ast = Procedure {
            name: object.name.clone(),
            params,
            body,
        };

        Ok(Self::from_ast(&object.name, &ast, &variables).with_var_ids(var_ids))
    }

    /// Emits complete C# source code (.cs) for this Procedure. A member access that does not resolve is
    /// written as the stored name; a build that depends on the output uses [`ProcedureClass::emit_checked`].
    pub fn emit(&self) -> String {
        let mut emitter = self.make_emitter();
        self.emit_with(&mut emitter)
    }

    fn make_emitter(&self) -> CSharpEmitter<'_> {
        let mut emitter = CSharpEmitter::with_sdts(&self.variables, &self.sdts);
        if !self.navigation_levels.is_empty() {
            emitter.set_navigation_levels(&self.navigation_levels);
        }
        if self.sdk_names() {
            emitter = emitter
                .with_var_ids(&self.var_ids)
                .with_subroutines(self.subroutines.iter().map(|(n, _)| n.clone()).collect());
        }
        match &self.calls {
            Some(calls) => emitter.with_calls(calls.as_ref()),
            None => emitter,
        }
    }

    /// Emits the C# source, or an error naming every member access that does not resolve against the
    /// SDTs this build emits. A Procedure whose members cannot be resolved is not written.
    pub fn emit_checked(&self) -> Result<String, String> {
        self.emit_detailed().code
    }

    /// The wrapper file of a split main program: the plain-named class the SDK writes beside the
    /// `a`-prefixed class. It starts the `a` class through `ClassLoader` by name. `None` for a Procedure
    /// with parameters, which has no wrapper.
    pub fn emit_wrapper(&self) -> Option<String> {
        if !self.is_split() {
            return None;
        }
        let class = self.class_name();
        let a_class = self.emitted_class_name();
        let mut out = String::new();
        for imp in crate::csharp::types::STANDARD_IMPORTS {
            let _ = writeln!(out, "using {imp};");
            // The SDK's wrapper imports the data namespaces too (dcm37961700.cs and every wrapper).
            if *imp == "GeneXus.Cryptography" && self.sdk_names() {
                let _ = writeln!(out, "using System.Data;");
                let _ = writeln!(out, "using GeneXus.Data;");
            }
        }
        let _ = writeln!(out, "namespace {} {{", self.namespace);
        let _ = writeln!(out, "   public class {class} : GXProcedure");
        let _ = writeln!(out, "   {{");
        let _ = writeln!(out, "      public {class}( )");
        let _ = writeln!(out, "      {{");
        let _ = writeln!(out, "         context = new GxContext(  );");
        let _ = writeln!(out, "         DataStoreUtil.LoadDataStores( context);");
        let _ = writeln!(out, "         IsMain = true;");
        let _ = writeln!(out, "      }}");
        let _ = writeln!(out);
        let _ = writeln!(out, "      public {class}( IGxContext context )");
        let _ = writeln!(out, "      {{");
        let _ = writeln!(out, "         this.context = context;");
        let _ = writeln!(out, "         IsMain = false;");
        let _ = writeln!(out, "      }}");
        let _ = writeln!(out);
        let _ = writeln!(out, "      public void execute( )");
        let _ = writeln!(out, "      {{");
        let _ = writeln!(out, "         initialize();");
        let _ = writeln!(out, "         ExecuteImpl();");
        let _ = writeln!(out, "      }}");
        let _ = writeln!(out);
        let _ = writeln!(out, "      public void executeSubmit( )");
        let _ = writeln!(out, "      {{");
        let _ = writeln!(out, "         SubmitImpl();");
        let _ = writeln!(out, "      }}");
        let _ = writeln!(out);
        let _ = writeln!(out, "      protected override void ExecutePrivate( )");
        let _ = writeln!(out, "      {{");
        let _ = writeln!(out, "         /* GeneXus formulas */");
        let _ = writeln!(out, "         /* Output device settings */");
        let _ = writeln!(out, "         args = new Object[] {{}} ;");
        let _ = writeln!(
            out,
            "         ClassLoader.Execute(\"{a_class}\",\"{ns}\",\"{a_class}\", new Object[] {{context }}, \"execute\", args);",
            ns = self.namespace
        );
        let _ = writeln!(out, "         cleanup();");
        let _ = writeln!(out, "      }}");
        let _ = writeln!(out);
        let _ = writeln!(out, "      public override void cleanup( )");
        let _ = writeln!(out, "      {{");
        let _ = writeln!(out, "         CloseCursors();");
        let _ = writeln!(out, "         if ( IsMain )");
        let _ = writeln!(out, "         {{");
        let _ = writeln!(out, "            context.CloseConnections();");
        let _ = writeln!(out, "         }}");
        let _ = writeln!(out, "      }}");
        let _ = writeln!(out);
        let _ = writeln!(out, "      public override void initialize( )");
        let _ = writeln!(out, "      {{");
        let _ = writeln!(out, "         /* GeneXus formulas. */");
        let _ = writeln!(out, "      }}");
        let _ = writeln!(out);
        let _ = writeln!(out, "      private Object[] args ;");
        let _ = writeln!(out, "   }}");
        let _ = writeln!(out);
        let _ = writeln!(out, "}}");
        Some(out)
    }

    /// The checked emission, with the Procedures its call sites name. A build generates a caller only
    /// when every callee it names is generated too.
    pub fn emit_detailed(&self) -> CheckedEmit {
        let mut emitter = self.make_emitter();
        let out = self.emit_with(&mut emitter);
        let mut refusals = emitter.refusals();
        for (name, data_type) in self
            .variables
            .iter()
            .map(|(name, data_type)| (name.as_str(), data_type))
            .chain(
                self.params
                    .iter()
                    .map(|param| (param.name.as_str(), &param.data_type)),
            )
        {
            if matches!(data_type, DataType::BaseCollection { .. }) {
                if let Err(error) = crate::model::validate_data_type(data_type) {
                    refusals.push(format!("collection '{name}': {error}"));
                }
            }
        }
        CheckedEmit {
            code: if refusals.is_empty() {
                Ok(out)
            } else {
                Err(refusals.join("; "))
            },
            callees: emitter.callees(),
        }
    }

    fn emit_with(&self, emitter: &mut CSharpEmitter<'_>) -> String {
        let mut out = String::with_capacity(4096 + self.body.len() * 64);

        // 1. Standard imports, then the module namespaces of the build's SDTs
        for imp in crate::csharp::types::STANDARD_IMPORTS {
            let _ = writeln!(out, "using {imp};");
        }
        for namespace in self
            .usings
            .iter()
            .filter(|n| **n != self.namespace && !self.sdk_names() && self.uses_namespace(n))
        {
            let _ = writeln!(out, "using {namespace};");
        }

        // 2. Namespace
        let _ = writeln!(out, "namespace {} {{", self.namespace);

        // 3. Class declaration
        let _ = writeln!(
            out,
            "   public class {} : GXProcedure",
            self.emitted_class_name()
        );
        let _ = writeln!(out, "   {{");

        if self.is_main || self.is_split() {
            self.emit_main_entrypoint(&mut out);
        }

        self.emit_constructors(&mut out);

        self.emit_execute_methods(&mut out);

        self.emit_execute_private(&mut out, emitter);
        if self.sdk_names() {
            // The SDK writes the subroutines after `ExecutePrivate` (listprograms.cs).
            self.emit_sdk_subroutines(&mut out, emitter);
            self.emit_cleanup_and_initialize(&mut out);
        } else {
            self.emit_cleanup_and_initialize(&mut out);
            self.emit_subroutines(&mut out, emitter);
        }
        if !self.navigation_levels.is_empty() {
            self.emit_cursor_definitions(&mut out);
        }
        self.emit_member_fields(&mut out, &emitter.temp_declarations());

        let _ = writeln!(out, "   }}");
        let _ = writeln!(out);
        let _ = writeln!(out, "}}");

        if self.sdk_names() {
            return self.qualify_module_sdts(out);
        }
        out
    }

    /// The SDK names an SDT of a module by its full name and imports no module namespace
    /// (`GeneXus.Programs.general.ui.SdtProgramNames_ProgramName`, general/ui/listprograms.cs).
    fn qualify_module_sdts(&self, code: String) -> String {
        let mut code = code;
        for class in self.sdts.classes() {
            if class.namespace == "GeneXus.Programs" || class.namespace.is_empty() {
                continue;
            }
            let name = class.class_name();
            let qualified = format!("{}.{name}", class.namespace);
            let word = |c: char| c.is_ascii_alphanumeric() || c == '_';
            let mut out = String::with_capacity(code.len());
            let mut rest = code.as_str();
            while let Some(at) = rest.find(&name) {
                let before = rest[..at].chars().next_back();
                let after = rest[at + name.len()..].chars().next();
                out.push_str(&rest[..at]);
                if before.is_some_and(|c| word(c) || c == '.') || after.is_some_and(word) {
                    out.push_str(&name);
                } else {
                    out.push_str(&qualified);
                }
                rest = &rest[at + name.len()..];
            }
            out.push_str(rest);
            code = out;
        }
        code
    }

    /// Whether a variable or parameter of this class is typed by an SDT the build declares in
    /// `namespace`. The SDK imports no module namespace, so one is imported only when a type needs it.
    fn uses_namespace(&self, namespace: &str) -> bool {
        if !self.sdk_names() {
            return true;
        }
        self.variables
            .values()
            .chain(self.params.iter().map(|p| &p.data_type))
            .filter_map(|data_type| match data_type {
                DataType::Record { .. } | DataType::Collection { .. } => {
                    Some(to_csharp_type(data_type))
                }
                DataType::BaseCollection { item, .. } => Some(item.clone()),
                _ => None,
            })
            .any(|class| {
                self.sdts
                    .class(&class)
                    .is_some_and(|sdt| sdt.namespace == namespace)
            })
    }

    fn emit_main_entrypoint(&self, out: &mut String) {
        let _ = writeln!(out, "      public static int Main( string[] args )");
        let _ = writeln!(out, "      {{");
        let _ = writeln!(
            out,
            "         return new {}().MainImpl(args); ;",
            self.emitted_class_name()
        );
        let _ = writeln!(out, "      }}");
        let _ = writeln!(out);
        let _ = writeln!(out, "      public int executeCmdLine( string[] args )");
        let _ = writeln!(out, "      {{");
        let _ = writeln!(out, "         return ExecuteCmdLine(args); ;");
        let _ = writeln!(out, "      }}");
        let _ = writeln!(out);
        let _ = writeln!(
            out,
            "      protected override int ExecuteCmdLine( string[] args )"
        );
        let _ = writeln!(out, "      {{");
        if self.params.is_empty() {
            let _ = writeln!(out, "         execute();");
        } else {
            let mut cmd_args = Vec::new();
            for p in &self.params {
                match p.mode {
                    ParamMode::In => cmd_args.push(p.param_name.clone()),
                    ParamMode::Out => cmd_args.push(format!("out {}", p.param_name)),
                    ParamMode::InOut => cmd_args.push(format!("ref {}", p.param_name)),
                }
            }
            let _ = writeln!(out, "         execute({});", cmd_args.join(", "));
        }
        let _ = writeln!(out, "         return GX.GXRuntime.ExitCode ;");
        let _ = writeln!(out, "      }}");
        let _ = writeln!(out);
    }

    fn emit_constructors(&self, out: &mut String) {
        let _ = writeln!(out, "      public {}( )", self.emitted_class_name());
        let _ = writeln!(out, "      {{");
        let _ = writeln!(out, "         context = new GxContext(  );");
        let _ = writeln!(out, "         DataStoreUtil.LoadDataStores( context);");
        let _ = writeln!(out, "         IsMain = true;");
        if let Some(theme) = &self.theme {
            let _ = writeln!(out, "         context.SetDefaultTheme(\"{theme}\", true);");
        }
        let _ = writeln!(out, "      }}");
        let _ = writeln!(out);
        let _ = writeln!(
            out,
            "      public {}( IGxContext context )",
            self.emitted_class_name()
        );
        let _ = writeln!(out, "      {{");
        let _ = writeln!(out, "         this.context = context;");
        let _ = writeln!(out, "         IsMain = false;");
        let _ = writeln!(out, "      }}");
        let _ = writeln!(out);
    }

    /// The parameter list of a method whose declaration up to its `(` is `head`: `( )` when empty, else
    /// the SDK's `( p0 ,` with each further parameter on its own line, aligned after the `( `.
    fn parameter_list<'p>(head: &str, params: impl Iterator<Item = &'p CsharpParam>) -> String {
        let sigs: Vec<String> = params
            .map(|p| to_csharp_param_signature(p.mode, &p.data_type, &p.param_name))
            .collect();
        if sigs.is_empty() {
            return "( )".to_string();
        }
        let pad = " ".repeat(head.len() + 2);
        format!("( {} )", sigs.join(&format!(" ,\n{pad}")))
    }

    /// `this.AV8Hits = aP0_Hits;` for each input, the SDK's empty value for each output.
    fn emit_parameter_entry(&self, out: &mut String) {
        for p in &self.params {
            match p.mode {
                ParamMode::In | ParamMode::InOut => {
                    let _ = writeln!(out, "         this.{} = {};", p.var_name, p.param_name);
                }
                ParamMode::Out => {
                    let default_val = out_parameter_default(&p.data_type);
                    let _ = writeln!(out, "         this.{} = {} ;", p.var_name, default_val);
                }
            }
        }
    }

    /// `aP0_Hits=this.AV8Hits;` for each output (the SDK writes no spaces around `=`).
    fn emit_parameter_exit(&self, out: &mut String) {
        for p in &self.params {
            if matches!(p.mode, ParamMode::Out | ParamMode::InOut) {
                let _ = writeln!(out, "         {}=this.{};", p.param_name, p.var_name);
            }
        }
    }

    /// `execute`, then `executeUdp` when the last parameter is an output, then `executeSubmit`, in the
    /// SDK's order (dch37961700.cs).
    fn emit_execute_methods(&self, out: &mut String) {
        let head = "      public void execute";
        let _ = writeln!(
            out,
            "{head}{}",
            Self::parameter_list(head, self.params.iter())
        );
        let _ = writeln!(out, "      {{");
        self.emit_parameter_entry(out);
        let _ = writeln!(out, "         initialize();");
        let _ = writeln!(out, "         ExecuteImpl();");
        self.emit_parameter_exit(out);
        let _ = writeln!(out, "      }}");
        let _ = writeln!(out);

        self.emit_execute_udp(out);

        let head = "      public void executeSubmit";
        let _ = writeln!(
            out,
            "{head}{}",
            Self::parameter_list(head, self.params.iter())
        );
        let _ = writeln!(out, "      {{");
        self.emit_parameter_entry(out);
        let _ = writeln!(out, "         SubmitImpl();");
        self.emit_parameter_exit(out);
        let _ = writeln!(out, "      }}");
        let _ = writeln!(out);
    }

    /// The call arguments of `execute` from `executeUdp`: `ref aP0_Hits, out aP1_Result`.
    fn execute_arguments(&self) -> String {
        self.params
            .iter()
            .map(|p| match p.mode {
                ParamMode::In => p.param_name.clone(),
                ParamMode::Out => format!("out {}", p.param_name),
                ParamMode::InOut => format!("ref {}", p.param_name),
            })
            .collect::<Vec<_>>()
            .join(", ")
    }

    /// `executeUdp`: the SDK writes it when the last parameter is an output; it takes the other
    /// parameters and returns that one (`decimal executeUdp( ref decimal aP0_Hits )`, dch37961700.cs).
    fn emit_execute_udp(&self, out: &mut String) {
        if let Some((last, rest)) = self.params.split_last().filter(|(last, _)| {
            last.mode == ParamMode::Out || (self.sdk_names() && last.mode == ParamMode::InOut)
        }) {
            let head = format!(
                "      public {} executeUdp",
                to_csharp_type(&last.data_type)
            );
            let _ = writeln!(out, "{head}{}", Self::parameter_list(&head, rest.iter()));
            let _ = writeln!(out, "      {{");
            let _ = writeln!(out, "         execute({});", self.execute_arguments());
            let _ = writeln!(out, "         return {} ;", last.var_name);
            let _ = writeln!(out, "      }}");
            let _ = writeln!(out);
        } else if let Some(ret_type) = &self.return_type {
            let head = format!("      public {} executeUdp", to_csharp_type(ret_type));
            let _ = writeln!(
                out,
                "{head}{}",
                Self::parameter_list(&head, self.params.iter())
            );
            let _ = writeln!(out, "      {{");
            let _ = writeln!(out, "         execute({});", self.execute_arguments());
            let _ = writeln!(out, "         return AV_Result ;");
            let _ = writeln!(out, "      }}");
            let _ = writeln!(out);
        }
    }

    fn emit_execute_private(&self, out: &mut String, emitter: &mut CSharpEmitter) {
        let _ = writeln!(out, "      protected override void ExecutePrivate( )");
        let _ = writeln!(out, "      {{");
        let _ = writeln!(out, "         /* GeneXus formulas */");
        let _ = writeln!(out, "         /* Output device settings */");

        emitter.indented(|e| {
            e.indented(|e| {
                e.indented(|e| {
                    for stmt in &self.body {
                        out.push_str(&e.emit_stmt(stmt, &self.variables));
                    }
                });
            });
        });

        let _ = writeln!(out, "         cleanup();");
        let _ = writeln!(out, "      }}");
        let _ = writeln!(out);
    }

    /// `protected void S111( )`, its comment, and `returnInSub = false;` first (listprograms.cs).
    fn emit_sdk_subroutines(&self, out: &mut String, emitter: &mut CSharpEmitter) {
        for (sub_name, sub_body) in &self.subroutines {
            let method = emitter.subroutine_method(sub_name);
            let _ = writeln!(out, "      protected void {method}( )");
            let _ = writeln!(out, "      {{");
            let _ = writeln!(
                out,
                "         /* '{}' Routine */",
                sub_name.to_ascii_uppercase()
            );
            let _ = writeln!(out, "         returnInSub = false;");
            emitter.indented(|e| {
                e.indented(|e| {
                    e.indented(|e| {
                        let previous = e.in_subroutine.replace(true);
                        for stmt in sub_body {
                            out.push_str(&e.emit_stmt(stmt, &self.variables));
                        }
                        e.in_subroutine.set(previous);
                    });
                });
            });
            let _ = writeln!(out, "      }}");
            let _ = writeln!(out);
        }
    }

    fn emit_subroutines(&self, out: &mut String, emitter: &mut CSharpEmitter) {
        for (sub_name, sub_body) in &self.subroutines {
            let clean = clean_ident(sub_name);
            let _ = writeln!(out, "      private void Sub_{clean}( )");
            let _ = writeln!(out, "      {{");
            emitter.indented(|e| {
                e.indented(|e| {
                    e.indented(|e| {
                        for stmt in sub_body {
                            out.push_str(&e.emit_stmt(stmt, &self.variables));
                        }
                    });
                });
            });
            let _ = writeln!(out, "      }}");
            let _ = writeln!(out);
        }
    }

    fn emit_cursor_definitions(&self, out: &mut String) {
        if self.variables.is_empty() {
            crate::csharp::cursor::emit_cursor_definitions(&self.navigation_levels, out);
        } else {
            crate::csharp::cursor::emit_cursor_definitions_with_vars(
                &self.navigation_levels,
                &self.variables,
                out,
            );
        }
    }
}
