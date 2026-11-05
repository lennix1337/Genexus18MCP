//! Transaction C# class generator. Transactions derive from `GXDataArea`, the base the
//! GeneXus 18 runtime defines and its generated transactions use (`plan070trn`, `trn353`).

use crate::{
    catalog::{KbObject, ObjectKind},
    csharp::types::{
        clean_ident, to_csharp_attr_name, to_csharp_default_value, to_csharp_type,
        to_csharp_var_name,
    },
    model::DataType,
    text_tree::parse_transaction_structure,
    transaction::{
        TransactionAttributeRef, TransactionLevel, TransactionRule, TransactionStructure,
    },
};
use std::collections::BTreeMap;
use std::fmt::Write;

/// Extended standard imports for Transaction and Business Component classes.
pub use crate::csharp::types::EXTENDED_DATA_IMPORTS as TRANSACTION_IMPORTS;

/// Represents an emittable GeneXus C# Transaction class (`GXDataArea`).
#[derive(Debug, Clone)]
pub struct TransactionClass {
    pub name: String,
    pub namespace: String,
    pub theme: Option<String>,
    pub base_class: String,
    pub is_business_component: bool,
    pub structure: Option<TransactionStructure>,
    pub attributes: Vec<(String, DataType)>,
    pub variables: BTreeMap<String, DataType>,
}

impl TransactionClass {
    /// Creates a new TransactionClass with default settings.
    pub fn new(name: impl Into<String>) -> Self {
        Self {
            name: name.into(),
            namespace: "GeneXus.Programs".to_string(),
            theme: Some("KBTeste".to_string()),
            base_class: "GXDataArea".to_string(),
            is_business_component: false,
            structure: None,
            attributes: Vec::new(),
            variables: BTreeMap::new(),
        }
    }

    /// Creates a new TransactionClass from a TransactionStructure.
    pub fn from_structure(name: impl Into<String>, structure: TransactionStructure) -> Self {
        let is_bc = structure.is_business_component;
        let mut tc = Self::new(name);
        tc.is_business_component = is_bc;
        tc.structure = Some(structure);
        tc
    }

    /// Creates a new TransactionClass from a root TransactionLevel.
    pub fn from_level(name: impl Into<String>, root_level: TransactionLevel) -> Self {
        let structure = TransactionStructure::new(root_level);
        Self::from_structure(name, structure)
    }

    /// Sets the application theme.
    pub fn with_theme(mut self, theme: impl Into<String>) -> Self {
        self.theme = Some(theme.into());
        self
    }

    /// Sets custom namespace.
    pub fn with_namespace(mut self, namespace: impl Into<String>) -> Self {
        self.namespace = namespace.into();
        self
    }

    /// Sets base class (`GXDataArea` by default, or `TransactionBase`).
    pub fn with_base_class(mut self, base_class: impl Into<String>) -> Self {
        self.base_class = base_class.into();
        self
    }

    /// Sets whether Business Component API is enabled.
    pub fn with_business_component(mut self, enabled: bool) -> Self {
        self.is_business_component = enabled;
        if let Some(ref mut st) = self.structure {
            st.is_business_component = enabled;
        }
        self
    }

    /// Sets the TransactionStructure.
    pub fn with_structure(mut self, structure: TransactionStructure) -> Self {
        self.is_business_component = structure.is_business_component;
        self.structure = Some(structure);
        self
    }

    /// Adds a declared attribute.
    pub fn add_attribute(mut self, name: impl Into<String>, data_type: DataType) -> Self {
        self.attributes.push((name.into(), data_type));
        self
    }

    /// Adds a declared variable.
    pub fn add_variable(mut self, name: impl Into<String>, data_type: DataType) -> Self {
        self.variables.insert(name.into(), data_type);
        self
    }

    /// Constructs a TransactionClass directly from a portable `KbObject`.
    pub fn from_kb_object(object: &KbObject) -> Result<Self, String> {
        if object.kind != ObjectKind::Transaction {
            return Err(format!("expected Transaction, found {:?}", object.kind));
        }

        let mut structure_opt = None;
        let mut variables: BTreeMap<String, DataType> = BTreeMap::new();
        let mut extra_rules: Vec<TransactionRule> = Vec::new();

        for part in &object.source_parts {
            match part.name.as_str() {
                "STRUCTURE" | "TRANSACTION_STRUCTURE" => {
                    if !part.source.trim().is_empty() {
                        let st = parse_transaction_structure(&part.source)
                            .map_err(|e| format!("structure of '{}': {e}", object.name))?;
                        structure_opt = Some(st);
                    }
                }
                "VARIABLES" => {
                    if !part.source.trim().is_empty() {
                        variables =
                            serde_json::from_str::<BTreeMap<String, DataType>>(&part.source)
                                .map_err(|e| format!("variables of '{}': {e}", object.name))?;
                    }
                }
                "RULES" => {
                    if let Ok(rules) = serde_json::from_str::<Vec<TransactionRule>>(&part.source) {
                        extra_rules = rules;
                    }
                }
                _ => {}
            }
        }

        let mut tc = if let Some(mut structure) = structure_opt {
            for r in extra_rules {
                structure.rules.push(r);
            }
            Self::from_structure(&object.name, structure)
        } else {
            Self::new(&object.name)
        };

        tc.variables = variables;
        Ok(tc)
    }

    fn emit_usings(&self, out: &mut String) {
        for imp in TRANSACTION_IMPORTS {
            let _ = writeln!(out, "using {imp};");
        }
    }

    fn emit_constructors(&self, out: &mut String) {
        let _ = writeln!(out, "      public {}( )", self.name);
        let _ = writeln!(out, "      {{");
        let _ = writeln!(out, "         context = new GxContext(  );");
        let _ = writeln!(out, "         DataStoreUtil.LoadDataStores( context);");
        let _ = writeln!(out, "         IsMain = true;");
        if let Some(theme) = &self.theme {
            let _ = writeln!(out, "         context.SetDefaultTheme(\"{theme}\", true);");
        }
        let _ = writeln!(out, "      }}");
        let _ = writeln!(out);
        let _ = writeln!(out, "      public {}( IGxContext context )", self.name);
        let _ = writeln!(out, "      {{");
        let _ = writeln!(out, "         this.context = context;");
        let _ = writeln!(out, "         IsMain = false;");
        let _ = writeln!(out, "      }}");
        let _ = writeln!(out);
    }

    fn emit_execution_methods(&self, out: &mut String) {
        let _ = writeln!(out, "      public void execute( )");
        let _ = writeln!(out, "      {{");
        let _ = writeln!(out, "         initialize();");
        let _ = writeln!(out, "         ExecuteImpl();");
        let _ = writeln!(out, "      }}");
        let _ = writeln!(out);

        let _ = writeln!(out, "      protected override void ExecutePrivate( )");
        let _ = writeln!(out, "      {{");
        let _ = writeln!(out, "         cleanup();");
        let _ = writeln!(out, "      }}");
        let _ = writeln!(out);
    }

    fn emit_bc_methods(&self, out: &mut String) {
        if !self.is_business_component {
            return;
        }

        let key_attrs: Vec<&TransactionAttributeRef> = self
            .structure
            .as_ref()
            .map(|st| st.root.attributes.iter().filter(|a| a.is_key).collect())
            .unwrap_or_default();

        if !key_attrs.is_empty() {
            let load_params: Vec<String> = key_attrs
                .iter()
                .map(|a| {
                    let dt = a.data_type.as_ref().cloned().unwrap_or(DataType::Numeric {
                        precision: 10,
                        scale: 0,
                    });
                    let cs_type = to_csharp_type(&dt);
                    let clean = clean_ident(&a.name);
                    format!("{cs_type} aP_{clean}")
                })
                .collect();

            let _ = writeln!(out, "      public void Load( {} )", load_params.join(", "));
            let _ = writeln!(out, "      {{");
            let _ = writeln!(out, "         initialize();");
            for a in &key_attrs {
                let clean = clean_ident(&a.name);
                let attr_field = to_csharp_attr_name(&a.name);
                let _ = writeln!(out, "         this.{attr_field} = aP_{clean};");
            }
            let _ = writeln!(out, "         getByPrimaryKey();");
            let _ = writeln!(out, "      }}");
            let _ = writeln!(out);
        }

        let _ = writeln!(out, "      public void Load( )");
        let _ = writeln!(out, "      {{");
        let _ = writeln!(out, "         getByPrimaryKey();");
        let _ = writeln!(out, "      }}");
        let _ = writeln!(out);

        let _ = writeln!(out, "      public void Save( )");
        let _ = writeln!(out, "      {{");
        let _ = writeln!(out, "         ValidateRules();");
        let _ = writeln!(out, "         if ( Success() )");
        let _ = writeln!(out, "         {{");
        let _ = writeln!(out, "            CheckOptimisticConcurrency();");
        let _ = writeln!(out, "            if ( Success() )");
        let _ = writeln!(out, "            {{");
        let _ = writeln!(out, "               ExecuteSave();");
        let _ = writeln!(out, "            }}");
        let _ = writeln!(out);
        let _ = writeln!(out, "         }}");
        let _ = writeln!(out, "      }}");
        let _ = writeln!(out);

        let _ = writeln!(out, "      public void Insert( )");
        let _ = writeln!(out, "      {{");
        let _ = writeln!(out, "         Mode = \"INS\";");
        let _ = writeln!(out, "         Save();");
        let _ = writeln!(out, "      }}");
        let _ = writeln!(out);

        let _ = writeln!(out, "      public void Update( )");
        let _ = writeln!(out, "      {{");
        let _ = writeln!(out, "         Mode = \"UPD\";");
        let _ = writeln!(out, "         Save();");
        let _ = writeln!(out, "      }}");
        let _ = writeln!(out);

        let _ = writeln!(out, "      public void Delete( )");
        let _ = writeln!(out, "      {{");
        let _ = writeln!(out, "         ValidateRules();");
        let _ = writeln!(out, "         if ( Success() )");
        let _ = writeln!(out, "         {{");
        let _ = writeln!(out, "            ExecuteDelete();");
        let _ = writeln!(out, "         }}");
        let _ = writeln!(out, "      }}");
        let _ = writeln!(out);

        let _ = writeln!(out, "      public bool Success( )");
        let _ = writeln!(out, "      {{");
        let _ = writeln!(out, "         return (Errors == 0);");
        let _ = writeln!(out, "      }}");
        let _ = writeln!(out);

        let _ = writeln!(out, "      public bool Fail( )");
        let _ = writeln!(out, "      {{");
        let _ = writeln!(out, "         return (Errors > 0);");
        let _ = writeln!(out, "      }}");
        let _ = writeln!(out);

        let _ = writeln!(out, "      public msglist GetMessages( )");
        let _ = writeln!(out, "      {{");
        let _ = writeln!(out, "         return LclMsgLst;");
        let _ = writeln!(out, "      }}");
        let _ = writeln!(out);
    }

    fn attribute_data_type(&self, attribute: &str) -> DataType {
        if let Some(ref st) = self.structure {
            if let Some(attr) = st
                .root
                .attributes
                .iter()
                .find(|a| a.name.eq_ignore_ascii_case(attribute))
            {
                if let Some(ref dt) = attr.data_type {
                    return dt.clone();
                }
            }
            for lvl in &st.root.levels {
                if let Some(attr) = lvl
                    .attributes
                    .iter()
                    .find(|a| a.name.eq_ignore_ascii_case(attribute))
                {
                    if let Some(ref dt) = attr.data_type {
                        return dt.clone();
                    }
                }
            }
        }
        if let Some((_, dt)) = self
            .attributes
            .iter()
            .find(|(name, _)| name.eq_ignore_ascii_case(attribute))
        {
            return dt.clone();
        }
        DataType::Numeric {
            precision: 10,
            scale: 0,
        }
    }

    fn format_default_value_expr(&self, value: &str, dt: &DataType) -> String {
        let trimmed = value.trim();
        if let Some(var_name) = trimmed.strip_prefix('&') {
            return to_csharp_var_name(var_name);
        }
        if (trimmed.starts_with('\'') && trimmed.ends_with('\''))
            || (trimmed.starts_with('"') && trimmed.ends_with('"'))
        {
            let inner = &trimmed[1..trimmed.len() - 1].replace('"', "\\\"");
            return format!("\"{inner}\"");
        }
        match dt {
            DataType::Character { .. } | DataType::Varchar { .. } => {
                format!("\"{}\"", trimmed.replace('"', "\\\""))
            }
            DataType::Boolean {} => {
                if trimmed.eq_ignore_ascii_case("true") || trimmed == "1" {
                    "true".to_string()
                } else {
                    "false".to_string()
                }
            }
            DataType::Numeric { scale, .. } if *scale > 0 => {
                if trimmed.ends_with('m') || trimmed.ends_with('M') {
                    trimmed.to_string()
                } else if trimmed.parse::<f64>().is_ok() {
                    format!("{trimmed}m")
                } else {
                    to_csharp_default_value(dt)
                }
            }
            DataType::Numeric { .. } => {
                if trimmed.parse::<i64>().is_ok() {
                    trimmed.to_string()
                } else {
                    to_csharp_default_value(dt)
                }
            }
            _ => to_csharp_default_value(dt),
        }
    }

    fn translate_rule_condition(&self, cond: &str) -> String {
        let trimmed = cond.trim();
        if trimmed.is_empty() {
            return "true".to_string();
        }
        if let Some(target) = trimmed
            .strip_suffix(".IsEmpty()")
            .or_else(|| trimmed.strip_suffix(".isempty()"))
        {
            let target = target.trim();
            if let Some(var_name) = target.strip_prefix('&') {
                let cs_var = to_csharp_var_name(var_name);
                return format!("string.IsNullOrEmpty({cs_var}.ToString())");
            }
            let dt = self.attribute_data_type(target);
            let field = to_csharp_attr_name(target);
            return match dt {
                DataType::Character { .. } | DataType::Varchar { .. } => {
                    format!("string.IsNullOrEmpty({field})")
                }
                DataType::Date {} | DataType::DateTime {} => {
                    format!("({field} == DateTime.MinValue)")
                }
                DataType::Boolean {} => format!("(!{field})"),
                _ => format!("({field} == 0)"),
            };
        }
        "true".to_string()
    }

    fn emit_rules(&self, out: &mut String) {
        let _ = writeln!(out, "      public void ValidateRules( )");
        let _ = writeln!(out, "      {{");
        let _ = writeln!(out, "         Errors = 0;");
        let _ = writeln!(out, "         /* GeneXus validation rules */");

        if let Some(ref structure) = self.structure {
            for rule in &structure.rules {
                match rule {
                    TransactionRule::Default { attribute, value } => {
                        let attr_field = to_csharp_attr_name(attribute);
                        let dt = self.attribute_data_type(attribute);
                        let empty_check = match dt {
                            DataType::Character { .. } | DataType::Varchar { .. } => {
                                format!("string.IsNullOrEmpty({attr_field})")
                            }
                            DataType::Date {} | DataType::DateTime {} => {
                                format!("{attr_field} == DateTime.MinValue")
                            }
                            DataType::Boolean {} => format!("!{attr_field}"),
                            DataType::Numeric { .. } => format!("{attr_field} == 0"),
                            _ => format!("string.IsNullOrEmpty({attr_field}.ToString())"),
                        };
                        let value_expr = self.format_default_value_expr(value, &dt);
                        let _ = writeln!(out, "         /* Default rule: {attribute} = {value} */");
                        let _ = writeln!(out, "         if ( {empty_check} )");
                        let _ = writeln!(out, "         {{");
                        let _ = writeln!(out, "            {attr_field} = {value_expr};");
                        let _ = writeln!(out, "         }}");
                    }
                    TransactionRule::Error { message, condition } => {
                        let escaped_msg = message.replace('"', "\\\"");
                        let cond_expr = condition
                            .as_deref()
                            .map(|c| self.translate_rule_condition(c))
                            .unwrap_or_else(|| "true".to_string());
                        let _ = writeln!(out, "         if ( {cond_expr} )");
                        let _ = writeln!(out, "         {{");
                        let _ = writeln!(out, "            LclMsgLst.addItem(\"{escaped_msg}\");");
                        let _ = writeln!(out, "            Errors = (short)(Errors + 1);");
                        let _ = writeln!(out, "         }}");
                    }
                    TransactionRule::Msg { message, condition } => {
                        let escaped_msg = message.replace('"', "\\\"");
                        let cond_expr = condition
                            .as_deref()
                            .map(|c| self.translate_rule_condition(c))
                            .unwrap_or_else(|| "true".to_string());
                        let _ = writeln!(out, "         if ( {cond_expr} )");
                        let _ = writeln!(out, "         {{");
                        let _ = writeln!(out, "            LclMsgLst.addItem(\"{escaped_msg}\");");
                        let _ = writeln!(out, "         }}");
                    }
                    TransactionRule::NoAccept { attribute } => {
                        let _ = writeln!(out, "         /* NoAccept rule: {attribute} */");
                    }
                    TransactionRule::Serial { attribute, step } => {
                        let _ =
                            writeln!(out, "         /* Serial rule: {attribute}, step {step} */");
                    }
                    TransactionRule::Prompt { object, attributes } => {
                        let _ = writeln!(
                            out,
                            "         /* Prompt rule: {object} ({}) */",
                            attributes.join(", ")
                        );
                    }
                }
            }
        }

        let _ = writeln!(out, "      }}");
        let _ = writeln!(out);
    }

    fn emit_db_stubs(&self, out: &mut String) {
        let _ = writeln!(out, "      protected void getByPrimaryKey( )");
        let _ = writeln!(out, "      {{");
        let _ = writeln!(
            out,
            "         /* Read record from database using primary key */"
        );
        let _ = writeln!(out, "      }}");
        let _ = writeln!(out);

        let _ = writeln!(out, "      protected void CheckOptimisticConcurrency( )");
        let _ = writeln!(out, "      {{");
        let _ = writeln!(out, "         /* Check optimistic concurrency */");
        let _ = writeln!(out, "      }}");
        let _ = writeln!(out);

        let _ = writeln!(out, "      protected void ExecuteSave( )");
        let _ = writeln!(out, "      {{");
        let _ = writeln!(out, "         /* Save/Insert/Update record to database */");
        let _ = writeln!(out, "      }}");
        let _ = writeln!(out);

        let _ = writeln!(out, "      protected void ExecuteDelete( )");
        let _ = writeln!(out, "      {{");
        let _ = writeln!(out, "         /* Delete record from database */");
        let _ = writeln!(out, "      }}");
        let _ = writeln!(out);
    }

    fn emit_cleanup_and_init(&self, out: &mut String) {
        let _ = writeln!(out, "      public override void cleanup( )");
        let _ = writeln!(out, "      {{");
        let _ = writeln!(out, "         CloseCursors();");
        let _ = writeln!(out, "         if ( IsMain )");
        let _ = writeln!(out, "         {{");
        let _ = writeln!(out, "            context.CloseConnections();");
        let _ = writeln!(out, "         }}");
        let _ = writeln!(out, "         ExitApp();");
        let _ = writeln!(out, "      }}");
        let _ = writeln!(out);

        let _ = writeln!(out, "      public override void initialize( )");
        let _ = writeln!(out, "      {{");
        let _ = writeln!(out, "         Errors = 0;");
        let _ = writeln!(out, "         LclMsgLst = new msglist();");
        if let Some(ref structure) = self.structure {
            for attr in &structure.root.attributes {
                let attr_field = to_csharp_attr_name(&attr.name);
                let dt = attr
                    .data_type
                    .as_ref()
                    .cloned()
                    .unwrap_or(DataType::Numeric {
                        precision: 10,
                        scale: 0,
                    });
                let default_val = to_csharp_default_value(&dt);
                let _ = writeln!(out, "         {attr_field} = {default_val} ;");
            }
        }
        for (attr_name, dt) in &self.attributes {
            let attr_field = to_csharp_attr_name(attr_name);
            let default_val = to_csharp_default_value(dt);
            let _ = writeln!(out, "         {attr_field} = {default_val} ;");
        }
        for (var_name, data_type) in &self.variables {
            let cs_name = to_csharp_var_name(var_name);
            let default_val = to_csharp_default_value(data_type);
            let _ = writeln!(out, "         {cs_name} = {default_val} ;");
        }
        let _ = writeln!(out, "      }}");
        let _ = writeln!(out);
    }

    fn emit_properties(&self, out: &mut String) {
        if let Some(ref structure) = self.structure {
            for attr in &structure.root.attributes {
                let attr_field = to_csharp_attr_name(&attr.name);
                let clean = clean_ident(&attr.name);
                let dt = attr
                    .data_type
                    .as_ref()
                    .cloned()
                    .unwrap_or(DataType::Numeric {
                        precision: 10,
                        scale: 0,
                    });
                let cs_type = to_csharp_type(&dt);

                let _ = writeln!(out, "      public {cs_type} {clean}");
                let _ = writeln!(out, "      {{");
                let _ = writeln!(out, "         get {{");
                let _ = writeln!(out, "            return {attr_field} ;");
                let _ = writeln!(out, "         }}");
                let _ = writeln!(out, "         set {{");
                let _ = writeln!(out, "            {attr_field} = value ;");
                let _ = writeln!(out, "         }}");
                let _ = writeln!(out, "      }}");
                let _ = writeln!(out);
            }
        }
        for (attr_name, dt) in &self.attributes {
            let attr_field = to_csharp_attr_name(attr_name);
            let clean = clean_ident(attr_name);
            let cs_type = to_csharp_type(dt);

            let _ = writeln!(out, "      public {cs_type} {clean}");
            let _ = writeln!(out, "      {{");
            let _ = writeln!(out, "         get {{");
            let _ = writeln!(out, "            return {attr_field} ;");
            let _ = writeln!(out, "         }}");
            let _ = writeln!(out, "         set {{");
            let _ = writeln!(out, "            {attr_field} = value ;");
            let _ = writeln!(out, "         }}");
            let _ = writeln!(out, "      }}");
            let _ = writeln!(out);

            let _ = writeln!(
                out,
                "      public {cs_type} gxTpr_{clean} {{ get {{ return {attr_field}; }} set {{ {attr_field} = value; }} }}"
            );
            let _ = writeln!(out);
        }
    }

    fn emit_nested_levels(&self, out: &mut String) {
        if let Some(ref structure) = self.structure {
            for child_level in &structure.root.levels {
                let child_clean = clean_ident(&child_level.name);
                let _ = writeln!(out, "      public class {}_{child_clean}", self.name);
                let _ = writeln!(out, "      {{");
                for attr in &child_level.attributes {
                    let attr_field = to_csharp_attr_name(&attr.name);
                    let clean = clean_ident(&attr.name);
                    let dt = attr
                        .data_type
                        .as_ref()
                        .cloned()
                        .unwrap_or(DataType::Numeric {
                            precision: 10,
                            scale: 0,
                        });
                    let cs_type = to_csharp_type(&dt);

                    let _ = writeln!(out, "         private {cs_type} {attr_field} ;");
                    let _ = writeln!(out);
                    let _ = writeln!(out, "         public {cs_type} {clean}");
                    let _ = writeln!(out, "         {{");
                    let _ = writeln!(out, "            get {{ return {attr_field} ; }}");
                    let _ = writeln!(out, "            set {{ {attr_field} = value ; }}");
                    let _ = writeln!(out, "         }}");
                }
                let _ = writeln!(out, "      }}");
                let _ = writeln!(out);
            }
        }
    }

    fn emit_fields(&self, out: &mut String) {
        if let Some(ref structure) = self.structure {
            for attr in &structure.root.attributes {
                let attr_field = to_csharp_attr_name(&attr.name);
                let dt = attr
                    .data_type
                    .as_ref()
                    .cloned()
                    .unwrap_or(DataType::Numeric {
                        precision: 10,
                        scale: 0,
                    });
                let cs_type = to_csharp_type(&dt);
                let _ = writeln!(out, "      private {cs_type} {attr_field} ;");
            }
        }
        for (attr_name, dt) in &self.attributes {
            let attr_field = to_csharp_attr_name(attr_name);
            let cs_type = to_csharp_type(dt);
            let _ = writeln!(out, "      private {cs_type} {attr_field} ;");
        }

        for (var_name, data_type) in &self.variables {
            let cs_type = to_csharp_type(data_type);
            let cs_name = to_csharp_var_name(var_name);
            let _ = writeln!(out, "      private {cs_type} {cs_name} ;");
        }

        let _ = writeln!(out, "      private short Errors ;");
        let _ = writeln!(out, "      private msglist LclMsgLst ;");
    }

    /// Emits complete C# source code (.cs) for this Transaction / Business Component.
    pub fn emit(&self) -> String {
        let mut out = String::new();

        self.emit_usings(&mut out);
        let _ = writeln!(out, "namespace {} {{", self.namespace);
        let _ = writeln!(out, "   public class {} : {}", self.name, self.base_class);
        let _ = writeln!(out, "   {{");

        self.emit_constructors(&mut out);
        self.emit_execution_methods(&mut out);
        self.emit_bc_methods(&mut out);
        self.emit_rules(&mut out);

        self.emit_db_stubs(&mut out);
        self.emit_cleanup_and_init(&mut out);
        self.emit_properties(&mut out);
        self.emit_nested_levels(&mut out);
        self.emit_fields(&mut out);

        let _ = writeln!(out, "   }}");
        let _ = writeln!(out);
        let _ = writeln!(out, "}}");

        out
    }
}
