//! Parser for the deliberately small, numeric GeneXus-style source subset.
//!
//! This module accepts authored source bodies only. It does not read or decode
//! native Knowledge Bases, SDK storage, or generated artifacts.

pub(crate) fn is_typed_builtin(name: &str) -> bool {
    matches!(
        name,
        "NEW"
            | "YMDTOD"
            | "YMDHMSTOT"
            | "CTOD"
            | "DTOC"
            | "TTOC"
            | "STR"
            | "TRIM"
            | "LEN"
            | "SUBSTR"
            | "UPPER"
            | "LOWER"
            | "STRREPLACE"
            | "LTRIM"
            | "RTRIM"
            | "PADL"
            | "PADR"
            | "SPACE"
            | "CONCAT"
            | "STARTSWITH"
            | "ENDSWITH"
            | "CONTAINS"
            | "STRSEARCH"
            | "INT"
            | "ROUND"
            | "TRUNC"
            | "VAL"
            | "LEFT"
            | "RIGHT"
            | "ASC"
            | "CHR"
            | "NOTNUMERIC"
            | "LTRIMSTR"
            | "INDEXOF"
            | "DAY"
            | "MONTH"
            | "YEAR"
            | "HOUR"
            | "MINUTE"
            | "SEC"
            | "DOW"
            | "ADDYR"
            | "ADDMTH"
            | "ADDDAYS"
            | "DADD"
            | "TADD"
            | "DDIFF"
            | "TDIFF"
            | "AGE"
            | "ENDOFMONTH"
            | "RESETTIME"
            | "TOSTRING"
            | "IIF"
            | "ISEMPTY"
            | "NULLVALUE"
            | "FORMAT"
            | "NEWLINE"
            | "REPLICATE"
            | "CHARAT"
            | "STRSEARCHREV"
            | "BOOLTOSTR"
            | "TOBOOLEAN"
            | "STRTOBOOL"
            | "TOJSON"
            | "FROMJSON"
            | "TOXML"
            | "FROMXML"
    )
}

// The same bound the catalog applies to a source part (`catalog::MAX_SOURCE_PART_BYTES`): a body the
// catalog admits must also parse, so the two cannot drift apart.
const MAX_SOURCE_BYTES: usize = crate::catalog::MAX_SOURCE_PART_BYTES;
const MAX_TOKENS: usize = 100_000;
const MAX_NESTING: usize = 128;
const MAX_EXPRESSION_NODES: usize = 128;

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Procedure {
    pub name: String,
    pub params: Vec<String>,
    pub body: Vec<Stmt>,
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct FromXmlStmt {
    pub target: Box<Expr>,
    pub xml: Expr,
    pub root: Option<Expr>,
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct ForEachOrder {
    pub attribute: String,
    pub descending: bool,
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct DataSelectorRef {
    pub name: String,
    pub arguments: Vec<Expr>,
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct NewStmt {
    pub body: Vec<Stmt>,
    pub when_duplicate: Vec<Stmt>,
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct ForEachStmt {
    pub explicit_attributes: Vec<String>,
    pub using: Option<DataSelectorRef>,
    pub wheres: Vec<Expr>,
    pub orders: Vec<ForEachOrder>,
    pub defined_by: Option<String>,
    pub body: Vec<Stmt>,
    pub when_none: Vec<Stmt>,
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub enum Stmt {
    Assign(String, Expr),
    AssignField(Box<Expr>, String, Expr),
    CollectionAdd(Box<Expr>, Expr, Option<Expr>),
    CollectionRemove(Box<Expr>, Expr),
    CollectionClear(Box<Expr>),
    /// Deserializes JSON string into a Record or Collection: `&target.FromJson(&str)`
    FromJson(Box<Expr>, Expr),
    /// Deserializes XML string into a Record or Collection: `&target.FromXml(&str, [opt_root])`
    FromXml(Box<FromXmlStmt>),
    Print(Expr),
    MsgStatus(Expr),
    Return(Expr),
    ReturnVoid,
    If(Expr, Vec<Stmt>, Vec<Stmt>),
    While(Expr, Vec<Stmt>),
    /// Numeric For with an explicit, nonzero integer-literal stride.
    For(String, Expr, Expr, i64, Vec<Stmt>),
    /// For &Item in &Collection
    ForIn(String, Expr, Vec<Stmt>),
    /// GeneXus data access loop: `For Each ... EndFor` with optional When None
    ForEach(Box<ForEachStmt>),
    /// Attribute assignment inside For Each: `Attribute = expr`
    AssignAttribute(String, Expr),
    /// Delete current row in For Each: `delete`
    Delete,
    /// Insert row block: `new ... [when duplicate ...] endnew`
    New(Box<NewStmt>),
    /// Loop break statement
    Exit,
    /// `Proc(args)` or `Proc.Call(args)` as a statement; always holds an `Expr::Call`.
    Call(Expr),
    /// Call subroutine: `Do 'SubName'` or `Do SubName`
    DoSub(String),
    /// Subroutine definition: `Sub 'SubName' ... EndSub`
    Sub(String, Vec<Stmt>),
    /// Commit database transaction: `Commit` or `Commit()`
    Commit,
    /// Rollback database transaction: `Rollback` or `Rollback()`
    Rollback,
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub enum Expr {
    Integer(i64),
    Decimal(String),
    Text(String),
    TextLength(Box<Expr>),
    Member(Box<Expr>, String),
    CloneRecord(Box<Expr>),
    CollectionItem(Box<Expr>, Box<Expr>),
    DateMethod(String, Box<Expr>),
    DateShift(String, Box<Expr>, Box<Expr>),
    Boolean(bool),
    EmptyDate,
    Not(Box<Expr>),
    Variable(String),
    Attribute(String),
    Call(String, Vec<Expr>),
    Binary(Box<Expr>, BinOp, Box<Expr>),
    Negate(Box<Expr>),
    /// A parenthesised expression, kept only when the parser is asked to (see `keep_parentheses`): the
    /// SDK writes the source's parentheses (`(long)((21*2))`, arusttimingprobe.cs).
    Paren(Box<Expr>),
}

thread_local! {
    static KEEP_PARENTHESES: std::cell::Cell<bool> = const { std::cell::Cell::new(false) };
}

/// Parses with `f` keeping each parenthesised expression as `Expr::Paren`. Only the C# emitter that
/// writes the SDK's form reads these nodes; every other parse drops the parentheses, as before.
pub fn keep_parentheses<T>(f: impl FnOnce() -> T) -> T {
    let previous = KEEP_PARENTHESES.with(|keep| keep.replace(true));
    let result = f();
    KEEP_PARENTHESES.with(|keep| keep.set(previous));
    result
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum BinOp {
    And,
    Or,
    Add,
    Subtract,
    Multiply,
    Divide,
    Equal,
    NotEqual,
    Less,
    LessEqual,
    Greater,
    GreaterEqual,
}

/// The operand a Trim method accepts: a variable, a member, or the `ToString()` of one. The
/// `ToString()` form is the text of a value (`&I.ToString().Trim()`, 84 stored Procedures of KBTeste);
/// its type is decided where it is emitted, not here.
fn is_text_operand(value: &Expr) -> bool {
    match value {
        Expr::Variable(_) | Expr::Member(_, _) => true,
        // ToString() yields text. Its operand is a variable, a member, a Length() or a Year() whose numeric
        // value the emitter writes as the SDK does (`Trim(Length().ToString())`, `Trim(Year().ToString())`).
        // Other operands, such as arithmetic, are still refused here.
        Expr::Call(name, args) if name == "TOSTRING" => match args.as_slice() {
            [Expr::Variable(_) | Expr::Member(_, _) | Expr::TextLength(_)] => true,
            [Expr::DateMethod(method, _)] => method == "YEAR",
            _ => false,
        },
        _ => false,
    }
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
struct Location {
    line: usize,
    column: usize,
}

impl Location {
    fn error(self, message: impl std::fmt::Display) -> String {
        format!("line {}, column {}: {message}", self.line, self.column)
    }
}

#[derive(Debug, Clone, PartialEq, Eq)]
enum TokenKind {
    Integer(String),
    Decimal(String),
    Text(String),
    Identifier(String),
    Variable(String),
    Newline,
    Semicolon,
    LeftParen,
    RightParen,
    Dot,
    Comma,
    Plus,
    Minus,
    Star,
    Slash,
    Equals,
    NotEqual,
    Less,
    LessEqual,
    Greater,
    GreaterEqual,
    Eof,
}

#[derive(Debug, Clone, PartialEq, Eq)]
struct Token {
    kind: TokenKind,
    location: Location,
}

struct Lexer<'a> {
    source: &'a str,
    byte: usize,
    line: usize,
    column: usize,
    tokens: Vec<Token>,
}

impl<'a> Lexer<'a> {
    fn new(source: &'a str) -> Result<Self, String> {
        if source.len() > MAX_SOURCE_BYTES {
            return Err(Location { line: 1, column: 1 }.error(format_args!(
                "source exceeds the {MAX_SOURCE_BYTES}-byte limit"
            )));
        }

        Ok(Self {
            source,
            byte: 0,
            line: 1,
            column: 1,
            tokens: Vec::new(),
        })
    }

    fn peek(&self) -> Option<char> {
        self.source.get(self.byte..)?.chars().next()
    }

    fn peek_after_current(&self) -> Option<char> {
        let current = self.peek()?;
        let next_byte = self.byte + current.len_utf8();
        self.source.get(next_byte..)?.chars().next()
    }

    fn bump(&mut self) -> Option<char> {
        let ch = self.peek()?;
        self.byte += ch.len_utf8();
        if ch == '\r' {
            if self.peek() == Some('\n') {
                self.byte += 1;
            }
            self.line += 1;
            self.column = 1;
            Some('\n')
        } else if ch == '\n' {
            self.line += 1;
            self.column = 1;
            Some('\n')
        } else {
            self.column += 1;
            Some(ch)
        }
    }

    fn location(&self) -> Location {
        Location {
            line: self.line,
            column: self.column,
        }
    }

    fn push(&mut self, kind: TokenKind, location: Location) -> Result<(), String> {
        if self.tokens.len() >= MAX_TOKENS {
            return Err(location.error(format_args!("source exceeds the {MAX_TOKENS}-token limit")));
        }
        self.tokens.push(Token { kind, location });
        Ok(())
    }

    fn lex(mut self) -> Result<Vec<Token>, String> {
        while let Some(ch) = self.peek() {
            let start = self.location();
            match ch {
                ' ' | '\t' | '\u{000C}' => {
                    self.bump();
                }
                '\r' | '\n' => {
                    self.bump();
                    self.push(TokenKind::Newline, start)?;
                }
                '/' if self.peek_after_current() == Some('/') => {
                    self.bump();
                    self.bump();
                    while !matches!(self.peek(), None | Some('\r') | Some('\n')) {
                        self.bump();
                    }
                }
                '0'..='9' => {
                    let start_byte = self.byte;
                    while matches!(self.peek(), Some('0'..='9')) {
                        self.bump();
                    }
                    let kind = if self.peek() == Some('.') {
                        self.bump();
                        if !matches!(self.peek(), Some('0'..='9')) {
                            return Err(start.error("decimal point requires fractional digits"));
                        }
                        while matches!(self.peek(), Some('0'..='9')) {
                            self.bump();
                        }
                        TokenKind::Decimal(self.source[start_byte..self.byte].to_owned())
                    } else {
                        TokenKind::Integer(self.source[start_byte..self.byte].to_owned())
                    };
                    self.push(kind, start)?;
                }
                '"' | '\'' => {
                    let quote = self.bump().unwrap();
                    let mut value = String::new();
                    loop {
                        match self.bump() {
                            Some(ch) if ch == quote => break,
                            Some('\n') | None => {
                                return Err(start.error("unterminated text literal"));
                            }
                            Some(ch) => value.push(ch),
                        }
                    }
                    self.push(TokenKind::Text(value), start)?;
                }
                '&' => {
                    self.bump();
                    let name_start = self.byte;
                    if !matches!(self.peek(), Some(c) if c.is_ascii_alphabetic() || c == '_') {
                        return Err(start.error("expected an identifier after '&'"));
                    }
                    self.bump();
                    while matches!(self.peek(), Some(c) if c.is_ascii_alphanumeric() || c == '_') {
                        self.bump();
                    }
                    let name = self.source[name_start..self.byte].to_ascii_uppercase();
                    self.push(TokenKind::Variable(name), start)?;
                }
                c if c.is_ascii_alphabetic() || c == '_' => {
                    let start_byte = self.byte;
                    self.bump();
                    while matches!(self.peek(), Some(c) if c.is_ascii_alphanumeric() || c == '_') {
                        self.bump();
                    }
                    let name = self.source[start_byte..self.byte].to_ascii_uppercase();
                    self.push(TokenKind::Identifier(name), start)?;
                }
                '(' => self.single(TokenKind::LeftParen, start)?,
                ')' => self.single(TokenKind::RightParen, start)?,
                ',' => self.single(TokenKind::Comma, start)?,
                '.' => self.single(TokenKind::Dot, start)?,
                ';' => self.single(TokenKind::Semicolon, start)?,
                '+' => self.single(TokenKind::Plus, start)?,
                '-' => self.single(TokenKind::Minus, start)?,
                '*' => self.single(TokenKind::Star, start)?,
                '/' => self.single(TokenKind::Slash, start)?,
                '=' => self.single(TokenKind::Equals, start)?,
                '<' => {
                    self.bump();
                    let kind = match self.peek() {
                        Some('=') => {
                            self.bump();
                            TokenKind::LessEqual
                        }
                        Some('>') => {
                            self.bump();
                            TokenKind::NotEqual
                        }
                        _ => TokenKind::Less,
                    };
                    self.push(kind, start)?;
                }
                '>' => {
                    self.bump();
                    let kind = if self.peek() == Some('=') {
                        self.bump();
                        TokenKind::GreaterEqual
                    } else {
                        TokenKind::Greater
                    };
                    self.push(kind, start)?;
                }
                other => {
                    return Err(start.error(format_args!("unsupported character {other:?}")));
                }
            }
        }

        self.push(TokenKind::Eof, self.location())?;
        Ok(self.tokens)
    }

    fn single(&mut self, kind: TokenKind, location: Location) -> Result<(), String> {
        self.bump();
        self.push(kind, location)
    }
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
enum BlockEnd {
    Else,
    Case,
    Otherwise,
    EndIf,
    EndDo,
    EndCase,
    EndFor,
    EndSub,
    WhenNone,
    WhenDuplicate,
    EndNew,
    Eof,
}

struct CaseSections {
    cases: Vec<(Expr, Vec<Stmt>)>,
    otherwise: Vec<Stmt>,
}

struct Parser {
    tokens: Vec<Token>,
    cursor: usize,
    allow_attributes: bool,
}

impl Parser {
    fn new(tokens: Vec<Token>) -> Self {
        Self {
            tokens,
            cursor: 0,
            allow_attributes: false,
        }
    }

    fn current(&self) -> &Token {
        // The lexer always appends EOF, and parsing never advances beyond it.
        &self.tokens[self.cursor]
    }

    fn at(&self, kind: &TokenKind) -> bool {
        &self.current().kind == kind
    }

    fn at_keyword(&self, keyword: &str) -> bool {
        matches!(&self.current().kind, TokenKind::Identifier(name) if name == keyword)
    }

    fn next_is_keyword(&self, keyword: &str) -> bool {
        matches!(
            self.tokens.get(self.cursor + 1).map(|token| &token.kind),
            Some(TokenKind::Identifier(name)) if name == keyword
        )
    }

    fn take(&mut self) -> Token {
        let token = self.current().clone();
        if !matches!(token.kind, TokenKind::Eof) {
            self.cursor += 1;
        }
        token
    }

    fn expect(&mut self, kind: TokenKind, expected: &str) -> Result<Token, String> {
        if self.at(&kind) {
            Ok(self.take())
        } else {
            Err(self
                .current()
                .location
                .error(format_args!("expected {expected}")))
        }
    }

    fn expect_keyword(&mut self, keyword: &str) -> Result<Token, String> {
        if self.at_keyword(keyword) {
            Ok(self.take())
        } else {
            Err(self
                .current()
                .location
                .error(format_args!("expected keyword {keyword}")))
        }
    }

    fn skip_separators(&mut self) {
        while matches!(
            self.current().kind,
            TokenKind::Newline | TokenKind::Semicolon
        ) {
            self.take();
        }
    }

    fn require_statement_end(&self) -> Result<(), String> {
        if matches!(
            self.current().kind,
            TokenKind::Newline | TokenKind::Semicolon | TokenKind::Eof
        ) {
            Ok(())
        } else {
            Err(self
                .current()
                .location
                .error("expected a newline or ';' after the statement"))
        }
    }

    fn parse_body(mut self) -> Result<Vec<Stmt>, String> {
        let (body, end) = self.parse_block(0)?;
        if end != BlockEnd::Eof {
            return Err(self.current().location.error(format_args!(
                "unexpected block terminator {}",
                self.block_end_name(end)
            )));
        }
        Ok(body)
    }

    fn parse_block(&mut self, nesting: usize) -> Result<(Vec<Stmt>, BlockEnd), String> {
        let mut statements = Vec::new();
        // Whether a Return or Exit already ended this block, and whether the dead code that follows it
        // was reported (one report per block).
        let mut terminated = false;
        let mut dead_reported = false;
        loop {
            self.skip_separators();
            let end = if self.at_keyword("ELSE") {
                Some(BlockEnd::Else)
            } else if self.at_keyword("CASE") {
                Some(BlockEnd::Case)
            } else if self.at_keyword("OTHERWISE") {
                Some(BlockEnd::Otherwise)
            } else if self.at_keyword("ENDIF") {
                Some(BlockEnd::EndIf)
            } else if self.at_keyword("ENDDO") {
                Some(BlockEnd::EndDo)
            } else if self.at_keyword("ENDCASE") {
                Some(BlockEnd::EndCase)
            } else if self.at_keyword("ENDFOR") {
                Some(BlockEnd::EndFor)
            } else if self.at_keyword("ENDSUB") {
                Some(BlockEnd::EndSub)
            } else if self.at_keyword("WHEN") && self.next_is_keyword("NONE") {
                self.take();
                self.take();
                Some(BlockEnd::WhenNone)
            } else if self.at_keyword("WHEN") && self.next_is_keyword("DUPLICATE") {
                self.take();
                self.take();
                Some(BlockEnd::WhenDuplicate)
            } else if self.at_keyword("ENDNEW") {
                Some(BlockEnd::EndNew)
            } else if self.at(&TokenKind::Eof) {
                Some(BlockEnd::Eof)
            } else {
                None
            };
            if let Some(end) = end {
                return Ok((statements, end));
            }

            let start = self.cursor;
            let location = self.current().location;
            let statement = self.parse_statement(nesting)?;
            // A subroutine definition is a declaration reached by `Do`, not code that runs in line.
            if terminated && !dead_reported && !matches!(statement, Stmt::Sub(..)) {
                dead_reported = true;
                record_unreachable(location);
            }
            terminated |= matches!(statement, Stmt::Return(_) | Stmt::ReturnVoid | Stmt::Exit);
            statements.push(statement);
            if self.cursor == start {
                return Err(self.current().location.error("parser made no progress"));
            }
            self.require_statement_end()?;
        }
    }

    #[inline(never)]
    fn parse_variable_statement(&mut self) -> Result<Stmt, String> {
        let location = self.current().location;
        let mut nodes = 0;
        let target = self.parse_primary(1, &mut nodes)?;
        if let Expr::Call(ref name, ref args) = target
            && matches!(name.as_str(), "FROMJSON" | "FROMJSONSTRING")
            && args.len() == 2
        {
            return Ok(Stmt::FromJson(Box::new(args[0].clone()), args[1].clone()));
        }
        if let Expr::Call(ref name, ref args) = target
            && name == "FROMXML"
            && (args.len() == 2 || args.len() == 3)
        {
            return Ok(Stmt::FromXml(Box::new(FromXmlStmt {
                target: Box::new(args[0].clone()),
                xml: args[1].clone(),
                root: if args.len() == 3 {
                    Some(args[2].clone())
                } else {
                    None
                },
            })));
        }
        if self.at(&TokenKind::Dot) {
            self.take();
            if self.at_keyword("SETEMPTY") {
                self.take();
                self.expect(TokenKind::LeftParen, "'(' after SetEmpty")?;
                self.expect(TokenKind::RightParen, "')' after SetEmpty(")?;
                return match target {
                    Expr::Variable(name) => Ok(Stmt::Assign(name, Expr::EmptyDate)),
                    Expr::Member(receiver, field) => {
                        Ok(Stmt::AssignField(receiver, field, Expr::EmptyDate))
                    }
                    _ => Err(location.error("SetEmpty target must be a variable or record member")),
                };
            }
            if self.at_keyword("ADD") {
                self.take();
                self.expect(TokenKind::LeftParen, "'(' after Add")?;
                let value = self.parse_expression()?;
                let opt_idx = if self.at(&TokenKind::Comma) {
                    self.take();
                    Some(self.parse_expression()?)
                } else {
                    None
                };
                self.expect(TokenKind::RightParen, "')' after Add argument")?;
                return Ok(Stmt::CollectionAdd(Box::new(target), value, opt_idx));
            }
            if self.at_keyword("REMOVE") {
                self.take();
                self.expect(TokenKind::LeftParen, "'(' after Remove")?;
                let idx = self.parse_expression()?;
                self.expect(TokenKind::RightParen, "')' after Remove argument")?;
                return Ok(Stmt::CollectionRemove(Box::new(target), idx));
            }
            if self.at_keyword("CLEAR") {
                self.take();
                self.expect(TokenKind::LeftParen, "'(' after Clear")?;
                self.expect(TokenKind::RightParen, "')' after Clear()")?;
                return Ok(Stmt::CollectionClear(Box::new(target)));
            }
            if self.at_keyword("FROMJSON") || self.at_keyword("FROMJSONSTRING") {
                self.take();
                self.expect(TokenKind::LeftParen, "'(' after FromJson")?;
                let json_expr = self.parse_expression()?;
                self.expect(TokenKind::RightParen, "')' after FromJson argument")?;
                return Ok(Stmt::FromJson(Box::new(target), json_expr));
            }
            if self.at_keyword("FROMXML") {
                self.take();
                self.expect(TokenKind::LeftParen, "'(' after FromXml")?;
                let xml_expr = self.parse_expression()?;
                let opt_root = if self.current().kind == TokenKind::Comma {
                    self.take();
                    Some(self.parse_expression()?)
                } else {
                    None
                };
                self.expect(TokenKind::RightParen, "')' after FromXml argument")?;
                return Ok(Stmt::FromXml(Box::new(FromXmlStmt {
                    target: Box::new(target),
                    xml: xml_expr,
                    root: opt_root,
                })));
            }
            return Err(location.error("unsupported statement method on target"));
        }
        self.expect(TokenKind::Equals, "'=' in an assignment")?;
        let value = self.parse_expression()?;
        match target {
            Expr::Variable(name) => Ok(Stmt::Assign(name, value)),
            Expr::Member(receiver, field) => Ok(Stmt::AssignField(receiver, field, value)),
            _ => Err(location.error("assignment target must be a variable or record member")),
        }
    }

    #[inline(never)]
    fn parse_call_statement(&mut self, name: String) -> Result<Stmt, String> {
        let location = self.take().location;
        if self.at(&TokenKind::Dot) {
            self.take();
            if self.at_keyword("UDP") {
                self.take();
            } else {
                self.expect_keyword("CALL")?;
            }
        }
        if !self.at(&TokenKind::LeftParen) {
            return Err(location.error(format_args!(
                "unsupported statement starting with {name}; a call statement needs '('"
            )));
        }
        self.take();
        let mut nodes = 0;
        Ok(Stmt::Call(self.parse_call(name, 1, &mut nodes, location)?))
    }

    fn parse_statement(&mut self, nesting: usize) -> Result<Stmt, String> {
        match &self.current().kind {
            TokenKind::Variable(_) => self.parse_variable_statement(),
            TokenKind::Identifier(keyword) if keyword == "EXIT" => {
                self.take();
                Ok(Stmt::Exit)
            }
            TokenKind::Identifier(keyword) if keyword == "COMMIT" => {
                self.take();
                if matches!(self.current().kind, TokenKind::LeftParen) {
                    self.take();
                    self.expect(TokenKind::RightParen, "')' after Commit")?;
                }
                Ok(Stmt::Commit)
            }
            TokenKind::Identifier(keyword) if keyword == "ROLLBACK" => {
                self.take();
                if matches!(self.current().kind, TokenKind::LeftParen) {
                    self.take();
                    self.expect(TokenKind::RightParen, "')' after Rollback")?;
                }
                Ok(Stmt::Rollback)
            }
            TokenKind::Identifier(keyword) if keyword == "PRINT" => self.parse_print(),
            TokenKind::Identifier(keyword) if keyword == "MSG" => self.parse_msg_status(),
            TokenKind::Identifier(keyword) if keyword == "RETURN" => {
                self.take();
                if matches!(
                    self.current().kind,
                    TokenKind::Newline | TokenKind::Semicolon | TokenKind::Eof
                ) {
                    Ok(Stmt::ReturnVoid)
                } else {
                    let value = self.parse_expression()?;
                    Ok(Stmt::Return(value))
                }
            }
            TokenKind::Identifier(keyword) if keyword == "NEW" => self.parse_new(nesting),
            TokenKind::Identifier(keyword) if keyword == "DELETE" => {
                self.take();
                Ok(Stmt::Delete)
            }
            TokenKind::Identifier(keyword) if keyword == "FOR" => self.parse_for(nesting),
            TokenKind::Identifier(keyword) if keyword == "IF" => self.parse_if(nesting),
            TokenKind::Identifier(keyword) if keyword == "DO" => self.parse_do(nesting),
            TokenKind::Identifier(keyword) if keyword == "SUB" => self.parse_sub(nesting),
            TokenKind::Identifier(keyword)
                if matches!(
                    keyword.as_str(),
                    "ELSE" | "CASE" | "OTHERWISE" | "ENDIF" | "ENDDO" | "ENDCASE" | "ENDFOR" | "ENDSUB" | "WHILE" | "WHEN" | "ENDNEW"
                ) =>
            {
                Err(self
                    .current()
                    .location
                    .error(format_args!("unexpected keyword {keyword}")))
            }
            TokenKind::Identifier(name)
                if self.allow_attributes
                    && self
                        .tokens
                        .get(self.cursor + 1)
                        .is_some_and(|t| t.kind == TokenKind::Equals) =>
            {
                let att_name = name.clone();
                self.take();
                self.expect(TokenKind::Equals, "'=' in attribute assignment")?;
                let expression = self.parse_expression()?;
                Ok(Stmt::AssignAttribute(att_name, expression))
            }
            TokenKind::Identifier(name) => self.parse_call_statement(name.clone()),
            _ => Err(self
                .current()
                .location
                .error("expected assignment, Print(expr), Msg(text, status), Return, If, Do While, or Do Case")),
        }
    }

    fn parse_print(&mut self) -> Result<Stmt, String> {
        self.expect_keyword("PRINT")?;
        self.expect(TokenKind::LeftParen, "'(' after Print")?;
        let value = self.parse_expression()?;
        self.expect(TokenKind::RightParen, "')' after the Print expression")?;
        Ok(Stmt::Print(value))
    }

    fn parse_msg_status(&mut self) -> Result<Stmt, String> {
        self.expect_keyword("MSG")?;
        self.expect(TokenKind::LeftParen, "'(' after Msg")?;
        let value = self.parse_expression()?;
        self.expect(TokenKind::Comma, "',' before Msg status mode")?;
        self.expect_keyword("STATUS")?;
        self.expect(TokenKind::RightParen, "')' after Msg status")?;
        Ok(Stmt::MsgStatus(value))
    }

    fn parse_if(&mut self, nesting: usize) -> Result<Stmt, String> {
        let start = self.expect_keyword("IF")?;
        self.ensure_nesting(nesting, start.location)?;
        let condition = self.parse_expression()?;
        self.require_statement_end()?;

        let (then_body, end) = self.parse_block(nesting + 1)?;
        let else_body = match end {
            BlockEnd::Else => {
                self.take();
                self.require_statement_end()?;
                let (body, end) = self.parse_block(nesting + 1)?;
                if end != BlockEnd::EndIf {
                    return Err(self.current().location.error(format_args!(
                        "expected EndIf, found {}",
                        self.block_end_name(end)
                    )));
                }
                self.expect_keyword("ENDIF")?;
                body
            }
            BlockEnd::EndIf => {
                self.expect_keyword("ENDIF")?;
                Vec::new()
            }
            other => {
                return Err(self.current().location.error(format_args!(
                    "expected EndIf or Else, found {}",
                    self.block_end_name(other)
                )));
            }
        };

        Ok(Stmt::If(condition, then_body, else_body))
    }

    fn parse_do(&mut self, nesting: usize) -> Result<Stmt, String> {
        if self.next_is_keyword("CASE") {
            self.parse_do_case(nesting)
        } else if self.next_is_keyword("WHILE") {
            self.parse_while(nesting)
        } else {
            self.expect_keyword("DO")?;
            let name = match &self.current().kind {
                TokenKind::Text(name) => name.clone(),
                TokenKind::Identifier(name) => name.clone(),
                _ => {
                    return Err(self
                        .current()
                        .location
                        .error("expected subroutine name, While, or Case after Do"));
                }
            };
            self.take();
            Ok(Stmt::DoSub(name.to_ascii_uppercase()))
        }
    }

    fn parse_sub(&mut self, nesting: usize) -> Result<Stmt, String> {
        if nesting > 0 {
            return Err(self
                .current()
                .location
                .error("subroutines cannot be nested inside blocks"));
        }
        self.take();
        let name = match &self.current().kind {
            TokenKind::Text(name) => name.clone(),
            TokenKind::Identifier(name) => name.clone(),
            _ => {
                return Err(self
                    .current()
                    .location
                    .error("expected subroutine name after Sub"));
            }
        };
        self.take();
        self.require_statement_end()?;
        let (sub_body, end) = self.parse_block(nesting + 1)?;
        if end != BlockEnd::EndSub {
            return Err(self.current().location.error(format_args!(
                "expected EndSub, found {}",
                self.block_end_name(end)
            )));
        }
        self.expect_keyword("ENDSUB")?;
        Ok(Stmt::Sub(name.to_ascii_uppercase(), sub_body))
    }

    fn parse_do_case(&mut self, nesting: usize) -> Result<Stmt, String> {
        let start = self.expect_keyword("DO")?;
        self.ensure_nesting(nesting, start.location)?;
        self.expect_keyword("CASE")?;
        self.require_statement_end()?;

        let sections = self.parse_case_sections(nesting + 1)?;
        let mut lowered = sections.otherwise;
        for (condition, body) in sections.cases.into_iter().rev() {
            lowered = vec![Stmt::If(condition, body, lowered)];
        }
        Ok(lowered
            .into_iter()
            .next()
            .expect("Do Case requires at least one Case"))
    }

    fn parse_case_sections(&mut self, nesting: usize) -> Result<CaseSections, String> {
        let mut cases = Vec::new();
        loop {
            self.skip_separators();
            if self.at_keyword("OTHERWISE") {
                if cases.is_empty() {
                    return Err(self
                        .current()
                        .location
                        .error("Otherwise must follow a Case"));
                }
                let otherwise_nesting = nesting + cases.len();
                self.ensure_nesting(otherwise_nesting, self.current().location)?;
                self.take();
                self.require_statement_end()?;
                let (otherwise, end) = self.parse_block(otherwise_nesting)?;
                if end != BlockEnd::EndCase {
                    return Err(self.current().location.error(format_args!(
                        "expected EndCase after Otherwise, found {}",
                        self.block_end_name(end)
                    )));
                }
                self.expect_keyword("ENDCASE")?;
                return Ok(CaseSections { cases, otherwise });
            }

            if self.at_keyword("ENDCASE") {
                if cases.is_empty() {
                    return Err(self
                        .current()
                        .location
                        .error("Do Case requires at least one Case"));
                }
                self.take();
                return Ok(CaseSections {
                    cases,
                    otherwise: Vec::new(),
                });
            }

            if !self.at_keyword("CASE") {
                return Err(self.current().location.error(format_args!(
                    "expected Case, Otherwise, or EndCase, found {}",
                    self.current_token_name()
                )));
            }

            let case_nesting = nesting + cases.len();
            self.ensure_nesting(case_nesting, self.current().location)?;
            self.take();
            let condition = self.parse_expression()?;
            self.require_statement_end()?;
            let (body, end) = self.parse_block(case_nesting)?;
            cases.push((condition, body));
            match end {
                BlockEnd::Case | BlockEnd::Otherwise => {}
                BlockEnd::EndCase => {
                    self.expect_keyword("ENDCASE")?;
                    return Ok(CaseSections {
                        cases,
                        otherwise: Vec::new(),
                    });
                }
                other => {
                    return Err(self.current().location.error(format_args!(
                        "expected Case, Otherwise, or EndCase, found {}",
                        self.block_end_name(other)
                    )));
                }
            }
        }
    }

    fn current_token_name(&self) -> &'static str {
        match self.current().kind {
            TokenKind::Eof => "end of input",
            TokenKind::Newline | TokenKind::Semicolon => "statement separator",
            _ => "token",
        }
    }

    fn parse_for(&mut self, nesting: usize) -> Result<Stmt, String> {
        let start = self.expect_keyword("FOR")?;
        self.ensure_nesting(nesting, start.location)?;
        if self.at_keyword("EACH") {
            return self.parse_for_each(nesting, start);
        }
        let token = self.take();
        let TokenKind::Variable(variable) = token.kind else {
            return Err(token.location.error("For requires an iterator variable"));
        };
        if self.at_keyword("IN") {
            self.take();
            let collection = self.parse_expression()?;
            self.require_statement_end()?;
            let (body, end) = self.parse_block(nesting + 1)?;
            if end != BlockEnd::EndFor {
                return Err(self.current().location.error(format_args!(
                    "expected EndFor, found {}",
                    self.block_end_name(end)
                )));
            }
            self.expect_keyword("ENDFOR")?;
            return Ok(Stmt::ForIn(variable, collection, body));
        }
        self.expect(TokenKind::Equals, "'=' after For iterator")?;
        let initial = self.parse_expression()?;
        self.expect_keyword("TO")?;
        let limit = self.parse_expression()?;
        let step = if self.at_keyword("STEP") {
            self.take();
            match self.parse_expression()? {
                Expr::Integer(value) => value,
                Expr::Negate(value) => match *value {
                    Expr::Integer(value) => value.checked_neg().ok_or("For Step overflow")?,
                    _ => {
                        return Err(start
                            .location
                            .error("For Step currently requires an integer literal"));
                    }
                },
                _ => {
                    return Err(start
                        .location
                        .error("For Step currently requires an integer literal"));
                }
            }
        } else {
            1
        };
        // Step 0 is admitted for the C# path. The SDK writes `while (x <= end) { ...; x = (int)(x+0); }`
        // and the emitter writes the same test and increment as `for (...; x <= end; x += 0)`. The typed
        // backend still refuses Step 0 (`typed_backend.rs`), and the format_version 1 backend refuses every For.
        self.require_statement_end()?;
        let (body, end) = self.parse_block(nesting + 1)?;
        if end != BlockEnd::EndFor {
            return Err(self.current().location.error(format_args!(
                "expected EndFor, found {}",
                self.block_end_name(end)
            )));
        }
        self.expect_keyword("ENDFOR")?;
        Ok(Stmt::For(variable, initial, limit, step, body))
    }

    fn parse_for_each(&mut self, nesting: usize, _start: Token) -> Result<Stmt, String> {
        self.expect_keyword("EACH")?;

        let prev_allow_attributes = self.allow_attributes;
        self.allow_attributes = true;

        let mut explicit_attributes = Vec::new();
        let mut using = None;
        while !self.at(&TokenKind::Newline)
            && !self.at(&TokenKind::Semicolon)
            && !self.at(&TokenKind::Eof)
        {
            if self.at_keyword("WHERE")
                || self.at_keyword("ORDER")
                || self.at_keyword("DEFINED")
                || self.at_keyword("USING")
            {
                break;
            }
            if let TokenKind::Identifier(name) = &self.current().kind {
                explicit_attributes.push(name.clone());
                self.take();
                if self.at(&TokenKind::Comma) {
                    self.take();
                }
            } else {
                break;
            }
        }
        self.skip_separators();

        let mut wheres = Vec::new();
        let mut orders = Vec::new();
        let mut defined_by = None;

        loop {
            self.skip_separators();
            if self.at_keyword("USING") {
                self.take();
                if let TokenKind::Identifier(name) = &self.current().kind {
                    let ds_name = name.clone();
                    self.take();
                    let mut args = Vec::new();
                    if self.at(&TokenKind::LeftParen) {
                        self.take();
                        while !self.at(&TokenKind::RightParen) && !self.at(&TokenKind::Eof) {
                            let arg = self.parse_expression()?;
                            args.push(arg);
                            if self.at(&TokenKind::Comma) {
                                self.take();
                            } else {
                                break;
                            }
                        }
                        self.expect(TokenKind::RightParen, "')' after DataSelector arguments")?;
                    }
                    using = Some(DataSelectorRef {
                        name: ds_name,
                        arguments: args,
                    });
                } else {
                    self.allow_attributes = prev_allow_attributes;
                    return Err(self
                        .current()
                        .location
                        .error("expected DataSelector name after Using"));
                }
                self.skip_separators();
            } else if self.at_keyword("WHERE") {
                self.take();
                let cond = self.parse_expression()?;
                wheres.push(cond);
                self.skip_separators();
            } else if self.at_keyword("ORDER") {
                self.take();
                loop {
                    self.skip_separators();
                    if let TokenKind::Identifier(att) = &self.current().kind {
                        let att_name = att.clone();
                        self.take();
                        let mut descending = false;
                        if self.at_keyword("DESC") {
                            self.take();
                            descending = true;
                        } else if self.at_keyword("NONE") {
                            self.take();
                        }
                        orders.push(ForEachOrder {
                            attribute: att_name,
                            descending,
                        });
                        if self.at(&TokenKind::Comma) {
                            self.take();
                        } else {
                            break;
                        }
                    } else {
                        break;
                    }
                }
                self.skip_separators();
            } else if self.at_keyword("DEFINED") {
                self.take();
                self.expect_keyword("BY")?;
                if let TokenKind::Identifier(att) = &self.current().kind {
                    defined_by = Some(att.clone());
                    self.take();
                } else {
                    self.allow_attributes = prev_allow_attributes;
                    return Err(self
                        .current()
                        .location
                        .error("expected attribute after Defined By"));
                }
                self.skip_separators();
            } else {
                break;
            }
        }

        let (body, end) = self.parse_block(nesting + 1)?;
        let (when_none, final_end) = if end == BlockEnd::WhenNone {
            let (wn_body, wn_end) = self.parse_block(nesting + 1)?;
            (wn_body, wn_end)
        } else {
            (Vec::new(), end)
        };

        if final_end != BlockEnd::EndFor {
            self.allow_attributes = prev_allow_attributes;
            return Err(self.current().location.error(format_args!(
                "expected EndFor, found {}",
                self.block_end_name(final_end)
            )));
        }
        self.expect_keyword("ENDFOR")?;
        self.allow_attributes = prev_allow_attributes;

        Ok(Stmt::ForEach(Box::new(ForEachStmt {
            explicit_attributes,
            using,
            wheres,
            orders,
            defined_by,
            body,
            when_none,
        })))
    }

    fn parse_new(&mut self, nesting: usize) -> Result<Stmt, String> {
        self.expect_keyword("NEW")?;
        let prev_allow_attributes = self.allow_attributes;
        self.allow_attributes = true;
        self.skip_separators();

        let (body, end) = self.parse_block(nesting + 1)?;
        let (when_duplicate, final_end) = if end == BlockEnd::WhenDuplicate {
            let (wd_body, wd_end) = self.parse_block(nesting + 1)?;
            (wd_body, wd_end)
        } else {
            (Vec::new(), end)
        };

        if final_end != BlockEnd::EndNew {
            self.allow_attributes = prev_allow_attributes;
            return Err(self.current().location.error(format_args!(
                "expected EndNew, found {}",
                self.block_end_name(final_end)
            )));
        }
        self.expect_keyword("ENDNEW")?;
        self.allow_attributes = prev_allow_attributes;

        Ok(Stmt::New(Box::new(NewStmt {
            body,
            when_duplicate,
        })))
    }

    fn parse_while(&mut self, nesting: usize) -> Result<Stmt, String> {
        let start = self.expect_keyword("DO")?;
        self.ensure_nesting(nesting, start.location)?;
        self.expect_keyword("WHILE")?;
        let condition = self.parse_expression()?;
        self.require_statement_end()?;

        let (body, end) = self.parse_block(nesting + 1)?;
        if end != BlockEnd::EndDo {
            return Err(self.current().location.error(format_args!(
                "expected EndDo, found {}",
                self.block_end_name(end)
            )));
        }
        self.expect_keyword("ENDDO")?;
        Ok(Stmt::While(condition, body))
    }

    fn ensure_nesting(&self, nesting: usize, location: Location) -> Result<(), String> {
        if nesting >= MAX_NESTING {
            Err(location.error(format_args!(
                "block nesting exceeds the {MAX_NESTING}-level limit"
            )))
        } else {
            Ok(())
        }
    }

    fn block_end_name(&self, end: BlockEnd) -> &'static str {
        match end {
            BlockEnd::Else => "Else",
            BlockEnd::Case => "Case",
            BlockEnd::Otherwise => "Otherwise",
            BlockEnd::EndIf => "EndIf",
            BlockEnd::EndDo => "EndDo",
            BlockEnd::EndCase => "EndCase",
            BlockEnd::EndFor => "EndFor",
            BlockEnd::EndSub => "EndSub",
            BlockEnd::WhenNone => "When None",
            BlockEnd::WhenDuplicate => "When Duplicate",
            BlockEnd::EndNew => "EndNew",
            BlockEnd::Eof => "end of input",
        }
    }

    fn parse_expression(&mut self) -> Result<Expr, String> {
        let mut nodes = 0;
        self.parse_or(0, &mut nodes)
    }

    fn parse_or(&mut self, depth: usize, nodes: &mut usize) -> Result<Expr, String> {
        let mut expression = self.parse_and(depth, nodes)?;
        while matches!(&self.current().kind, TokenKind::Identifier(word) if word == "OR") {
            self.count_expression_node(nodes, self.current().location)?;
            self.take();
            let right = self.parse_and(depth, nodes)?;
            expression = Expr::Binary(Box::new(expression), BinOp::Or, Box::new(right));
        }
        Ok(expression)
    }

    fn parse_and(&mut self, depth: usize, nodes: &mut usize) -> Result<Expr, String> {
        let mut expression = self.parse_not(depth, nodes)?;
        while matches!(&self.current().kind, TokenKind::Identifier(word) if word == "AND") {
            self.count_expression_node(nodes, self.current().location)?;
            self.take();
            let right = self.parse_not(depth, nodes)?;
            expression = Expr::Binary(Box::new(expression), BinOp::And, Box::new(right));
        }
        Ok(expression)
    }

    fn parse_not(&mut self, depth: usize, nodes: &mut usize) -> Result<Expr, String> {
        self.ensure_expression_depth(depth)?;
        if matches!(&self.current().kind, TokenKind::Identifier(word) if word == "NOT") {
            let token = self.take();
            self.count_expression_node(nodes, token.location)?;
            let operand = self.parse_not(depth + 1, nodes)?;
            return Ok(Expr::Not(Box::new(operand)));
        }
        self.parse_comparison(depth, nodes)
    }

    fn parse_comparison(&mut self, depth: usize, nodes: &mut usize) -> Result<Expr, String> {
        let left = self.parse_additive(depth, nodes)?;
        let Some(op) = self.comparison_operator() else {
            return Ok(left);
        };
        self.count_expression_node(nodes, self.current().location)?;
        self.take();
        let right = self.parse_additive(depth, nodes)?;
        if self.comparison_operator().is_some() {
            return Err(self.current().location.error(
                "chained comparisons are unsupported; combine one comparison per expression",
            ));
        }
        Ok(Expr::Binary(Box::new(left), op, Box::new(right)))
    }

    fn comparison_operator(&self) -> Option<BinOp> {
        match self.current().kind {
            TokenKind::Equals => Some(BinOp::Equal),
            TokenKind::NotEqual => Some(BinOp::NotEqual),
            TokenKind::Less => Some(BinOp::Less),
            TokenKind::LessEqual => Some(BinOp::LessEqual),
            TokenKind::Greater => Some(BinOp::Greater),
            TokenKind::GreaterEqual => Some(BinOp::GreaterEqual),
            _ => None,
        }
    }

    fn parse_additive(&mut self, depth: usize, nodes: &mut usize) -> Result<Expr, String> {
        let mut expression = self.parse_multiplicative(depth, nodes)?;
        loop {
            let op = match self.current().kind {
                TokenKind::Plus => BinOp::Add,
                TokenKind::Minus => BinOp::Subtract,
                _ => break,
            };
            self.count_expression_node(nodes, self.current().location)?;
            self.take();
            let right = self.parse_multiplicative(depth, nodes)?;
            expression = Expr::Binary(Box::new(expression), op, Box::new(right));
        }
        Ok(expression)
    }

    fn parse_multiplicative(&mut self, depth: usize, nodes: &mut usize) -> Result<Expr, String> {
        let mut expression = self.parse_unary(depth, nodes)?;
        loop {
            let op = match self.current().kind {
                TokenKind::Star => BinOp::Multiply,
                TokenKind::Slash => BinOp::Divide,
                _ => break,
            };
            self.count_expression_node(nodes, self.current().location)?;
            self.take();
            let right = self.parse_unary(depth, nodes)?;
            expression = Expr::Binary(Box::new(expression), op, Box::new(right));
        }
        Ok(expression)
    }

    fn parse_unary(&mut self, depth: usize, nodes: &mut usize) -> Result<Expr, String> {
        self.ensure_expression_depth(depth)?;
        if self.at(&TokenKind::Minus) {
            let minus = self.take();
            if let TokenKind::Integer(digits) = &self.current().kind {
                let literal = format!("-{digits}");
                let value = literal.parse::<i64>().map_err(|_| {
                    minus
                        .location
                        .error(format_args!("integer literal is outside the i64 range"))
                })?;
                self.count_expression_node(nodes, minus.location)?;
                self.take();
                // `-0` is kept as written for the SDK's form (`AV8Value = -0;`, arustedge7476154929200.cs).
                if value == 0 && KEEP_PARENTHESES.with(std::cell::Cell::get) {
                    return Ok(Expr::Negate(Box::new(Expr::Integer(0))));
                }
                return Ok(Expr::Integer(value));
            }
            let operand = self.parse_unary(depth + 1, nodes)?;
            self.count_expression_node(nodes, minus.location)?;
            return Ok(Expr::Negate(Box::new(operand)));
        }
        self.parse_primary(depth, nodes)
    }

    fn parse_primary(&mut self, depth: usize, nodes: &mut usize) -> Result<Expr, String> {
        let mut value = self.parse_atom(depth, nodes)?;
        let mut methods = 0;
        while self.at(&TokenKind::Dot) {
            // Add, Remove, Clear, and SetEmpty are statement-only mutators, consumed by parse_statement.
            if self.tokens.get(self.cursor + 1).is_some_and(|t| {
                matches!(
                    &t.kind,
                    TokenKind::Identifier(name)
                        if matches!(name.as_str(), "ADD" | "REMOVE" | "CLEAR" | "SETEMPTY")
                )
            }) && self
                .tokens
                .get(self.cursor + 2)
                .is_some_and(|t| t.kind == TokenKind::LeftParen)
            {
                break;
            }
            methods += 1;
            self.ensure_expression_depth(depth + methods)?;
            let location = self.take().location;
            let method = self.take();
            let TokenKind::Identifier(name) = method.kind else {
                return Err(method.location.error("expected method name"));
            };
            if !self.at(&TokenKind::LeftParen) {
                self.count_expression_node(nodes, location)?;
                value = Expr::Member(Box::new(value), name);
                continue;
            }
            value = self.parse_method(
                &name,
                value,
                depth + methods,
                nodes,
                location,
                method.location,
            )?;
        }
        Ok(value)
    }

    /// Parses one postfix method call: `name` is the method identifier, `value`
    /// the receiver, and the cursor sits on the opening parenthesis. The whole
    /// argument-bearing method grammar lives here rather than in the postfix
    /// loop so `parse_primary`, the frame of the deepest expression recursion,
    /// keeps the stack budget the nesting-limit tests encode.
    fn parse_method(
        &mut self,
        name: &str,
        value: Expr,
        depth: usize,
        nodes: &mut usize,
        location: Location,
        method_location: Location,
    ) -> Result<Expr, String> {
        if name == "TRIM" {
            if !is_text_operand(&value) {
                return Err(
                    method_location.error("Trim() currently requires a text variable or member")
                );
            }
            self.take();
            self.expect(TokenKind::RightParen, "')' after Trim(")?;
            self.count_expression_node(nodes, location)?;
            return Ok(Expr::Call("TRIM".into(), vec![value]));
        }
        if matches!(name, "TRIMSTART" | "TRIMEND") {
            if !is_text_operand(&value) {
                return Err(method_location
                    .error("TrimStart()/TrimEnd() currently requires a text variable or member"));
            }
            self.take();
            self.expect(TokenKind::RightParen, "')' after trim method")?;
            self.count_expression_node(nodes, location)?;
            let builtin = if name == "TRIMSTART" {
                "LTRIM"
            } else {
                "RTRIM"
            };
            return Ok(Expr::Call(builtin.into(), vec![value]));
        }
        if matches!(name, "PADLEFT" | "PADRIGHT") {
            self.expect(TokenKind::LeftParen, "'(' after pad method")?;
            let size = self.parse_or(depth, nodes)?;
            let mut args = vec![value, size];
            if self.current().kind == TokenKind::Comma {
                self.take();
                let fill = self.parse_or(depth, nodes)?;
                args.push(fill);
            }
            self.expect(TokenKind::RightParen, "')' after pad arguments")?;
            self.count_expression_node(nodes, location)?;
            let builtin = if name == "PADLEFT" { "PADL" } else { "PADR" };
            return Ok(Expr::Call(builtin.into(), args));
        }
        if name == "CONCAT" {
            self.expect(TokenKind::LeftParen, "'(' after Concat(")?;
            let other = self.parse_or(depth, nodes)?;
            let mut args = vec![value, other];
            if self.current().kind == TokenKind::Comma {
                self.take();
                let sep = self.parse_or(depth, nodes)?;
                args.push(sep);
            }
            self.expect(TokenKind::RightParen, "')' after Concat arguments")?;
            self.count_expression_node(nodes, location)?;
            return Ok(Expr::Call("CONCAT".into(), args));
        }
        if matches!(name, "STARTSWITH" | "ENDSWITH" | "CONTAINS") {
            self.expect(TokenKind::LeftParen, "'(' after string method")?;
            let target = self.parse_or(depth, nodes)?;
            self.expect(TokenKind::RightParen, "')' after argument")?;
            self.count_expression_node(nodes, location)?;
            return Ok(Expr::Call(name.into(), vec![value, target]));
        }
        if name == "INDEXOF" {
            self.expect(TokenKind::LeftParen, "'(' after IndexOf(")?;
            let find = self.parse_or(depth, nodes)?;
            let mut args = vec![value, find];
            if self.current().kind == TokenKind::Comma {
                self.take();
                let start = self.parse_or(depth, nodes)?;
                args.push(start);
                self.expect(TokenKind::RightParen, "')' after IndexOf arguments")?;
                self.count_expression_node(nodes, location)?;
                return Ok(Expr::Call("STRSEARCH".into(), args));
            }
            self.expect(TokenKind::RightParen, "')' after IndexOf arguments")?;
            self.count_expression_node(nodes, location)?;
            return Ok(Expr::Call("INDEXOF".into(), args));
        }
        if matches!(name, "LASTINDEXOF" | "STRSEARCHREV") {
            self.expect(TokenKind::LeftParen, "'(' after LastIndexOf(")?;
            let find = self.parse_or(depth, nodes)?;
            let mut args = vec![value, find];
            if self.current().kind == TokenKind::Comma {
                self.take();
                let start = self.parse_or(depth, nodes)?;
                args.push(start);
            }
            self.expect(TokenKind::RightParen, "')' after LastIndexOf arguments")?;
            self.count_expression_node(nodes, location)?;
            return Ok(Expr::Call("STRSEARCHREV".into(), args));
        }
        if name == "CHARAT" {
            self.expect(TokenKind::LeftParen, "'(' after CharAt(")?;
            let idx = self.parse_or(depth, nodes)?;
            self.expect(TokenKind::RightParen, "')' after CharAt argument")?;
            self.count_expression_node(nodes, location)?;
            return Ok(Expr::Call("CHARAT".into(), vec![value, idx]));
        }
        if name == "TOBOOLEAN" {
            self.expect(TokenKind::LeftParen, "'(' after ToBoolean(")?;
            self.expect(TokenKind::RightParen, "')' after zero-argument ToBoolean()")?;
            self.count_expression_node(nodes, location)?;
            return Ok(Expr::Call("TOBOOLEAN".into(), vec![value]));
        }
        if matches!(name, "TOJSON" | "TOJSONSTRING") {
            self.expect(TokenKind::LeftParen, "'(' after ToJson(")?;
            self.expect(TokenKind::RightParen, "')' after zero-argument ToJson()")?;
            self.count_expression_node(nodes, location)?;
            return Ok(Expr::Call("TOJSON".into(), vec![value]));
        }
        if matches!(name, "FROMJSON" | "FROMJSONSTRING") {
            self.expect(TokenKind::LeftParen, "'(' after FromJson(")?;
            let json_arg = self.parse_or(depth, nodes)?;
            self.expect(TokenKind::RightParen, "')' after FromJson argument")?;
            self.count_expression_node(nodes, location)?;
            return Ok(Expr::Call("FROMJSON".into(), vec![value, json_arg]));
        }
        if matches!(name, "TOXML") {
            self.expect(TokenKind::LeftParen, "'(' after ToXml(")?;
            let mut args = vec![value];
            if !matches!(self.current().kind, TokenKind::RightParen) {
                let root_arg = self.parse_or(depth, nodes)?;
                args.push(root_arg);
            }
            self.expect(TokenKind::RightParen, "')' after ToXml argument")?;
            self.count_expression_node(nodes, location)?;
            return Ok(Expr::Call("TOXML".into(), args));
        }
        if matches!(name, "FROMXML") {
            self.expect(TokenKind::LeftParen, "'(' after FromXml(")?;
            let xml_arg = self.parse_or(depth, nodes)?;
            let mut args = vec![value, xml_arg];
            if self.current().kind == TokenKind::Comma {
                self.take();
                let root_arg = self.parse_or(depth, nodes)?;
                args.push(root_arg);
            }
            self.expect(TokenKind::RightParen, "')' after FromXml arguments")?;
            self.count_expression_node(nodes, location)?;
            return Ok(Expr::Call("FROMXML".into(), args));
        }
        if name == "CLONE" {
            self.take();
            self.expect(TokenKind::RightParen, "')' after Clone(")?;
            self.count_expression_node(nodes, location)?;
            return Ok(Expr::CloneRecord(Box::new(value)));
        }
        if name == "SUBSTRING" {
            self.expect(TokenKind::LeftParen, "'(' after Substring(")?;
            let start = self.parse_or(depth, nodes)?;
            self.expect(TokenKind::Comma, "',' between Substring arguments")?;
            let length = self.parse_or(depth, nodes)?;
            self.expect(TokenKind::RightParen, "')' after Substring arguments")?;
            self.count_expression_node(nodes, location)?;
            return Ok(Expr::Call("SUBSTR".into(), vec![value, start, length]));
        }
        if matches!(name, "TOUPPER" | "TOLOWER") {
            self.expect(TokenKind::LeftParen, "'(' after case method")?;
            self.expect(TokenKind::RightParen, "')' after zero-argument case method")?;
            self.count_expression_node(nodes, location)?;
            let builtin = if name == "TOUPPER" { "UPPER" } else { "LOWER" };
            return Ok(Expr::Call(builtin.into(), vec![value]));
        }
        if name == "REPLACE" {
            self.expect(TokenKind::LeftParen, "'(' after Replace(")?;
            let find = self.parse_or(depth, nodes)?;
            self.expect(TokenKind::Comma, "',' between Replace arguments")?;
            let replacement = self.parse_or(depth, nodes)?;
            self.expect(TokenKind::RightParen, "')' after Replace arguments")?;
            self.count_expression_node(nodes, location)?;
            return Ok(Expr::Call(
                "STRREPLACE".into(),
                vec![value, find, replacement],
            ));
        }
        if name == "ITEM" {
            self.take();
            let index = self.parse_or(depth, nodes)?;
            self.expect(TokenKind::RightParen, "')' after Item index")?;
            self.count_expression_node(nodes, location)?;
            return Ok(Expr::CollectionItem(Box::new(value), Box::new(index)));
        }
        if matches!(
            name,
            "ADDMONTHS"
                | "ADDYEARS"
                | "ADDDAYS"
                | "ADDHOURS"
                | "ADDMINUTES"
                | "ADDSECONDS"
                | "DIFFERENCE"
        ) {
            self.expect(TokenKind::LeftParen, "'(' after calendar method")?;
            let offset = self.parse_or(depth, nodes)?;
            self.expect(TokenKind::RightParen, "')' after calendar offset")?;
            self.count_expression_node(nodes, location)?;
            return Ok(Expr::DateShift(
                name.to_owned(),
                Box::new(value),
                Box::new(offset),
            ));
        }
        if name == "AGE" {
            self.expect(TokenKind::LeftParen, "'(' after Age(")?;
            let current = self.parse_or(depth, nodes)?;
            self.expect(TokenKind::RightParen, "')' after Age argument")?;
            self.count_expression_node(nodes, location)?;
            return Ok(Expr::Call("AGE".into(), vec![value, current]));
        }
        if matches!(name, "ENDOFMONTH" | "RESETTIME") {
            self.expect(TokenKind::LeftParen, "'(' after calendar method")?;
            self.expect(TokenKind::RightParen, "')' after zero-argument method")?;
            self.count_expression_node(nodes, location)?;
            return Ok(Expr::Call(name.into(), vec![value]));
        }
        if matches!(name, "DAYOFWEEK" | "DOW") {
            self.expect(TokenKind::LeftParen, "'(' after method")?;
            self.expect(TokenKind::RightParen, "')' after zero-argument method")?;
            self.count_expression_node(nodes, location)?;
            return Ok(Expr::Call("DOW".into(), vec![value]));
        }
        if matches!(name, "ROUND" | "TRUNC") {
            self.expect(TokenKind::LeftParen, "'(' after method")?;
            let digits = self.parse_or(depth, nodes)?;
            self.expect(TokenKind::RightParen, "')' after method argument")?;
            self.count_expression_node(nodes, location)?;
            return Ok(Expr::Call(name.into(), vec![value, digits]));
        }
        if name == "INT" {
            self.expect(TokenKind::LeftParen, "'(' after Int(")?;
            self.expect(TokenKind::RightParen, "')' after zero-argument Int()")?;
            self.count_expression_node(nodes, location)?;
            return Ok(Expr::Call("INT".into(), vec![value]));
        }
        if name == "TOSTRING" {
            self.expect(TokenKind::LeftParen, "'(' after ToString(")?;
            self.expect(TokenKind::RightParen, "')' after zero-argument ToString()")?;
            self.count_expression_node(nodes, location)?;
            return Ok(Expr::Call("TOSTRING".into(), vec![value]));
        }
        if name == "TONUMERIC" {
            self.expect(TokenKind::LeftParen, "'(' after ToNumeric(")?;
            self.expect(TokenKind::RightParen, "')' after zero-argument ToNumeric()")?;
            self.count_expression_node(nodes, location)?;
            return Ok(Expr::Call("VAL".into(), vec![value]));
        }
        if matches!(name, "LEFT" | "RIGHT") {
            self.expect(TokenKind::LeftParen, "'(' after method")?;
            let size = self.parse_or(depth, nodes)?;
            self.expect(TokenKind::RightParen, "')' after method argument")?;
            self.count_expression_node(nodes, location)?;
            return Ok(Expr::Call(name.into(), vec![value, size]));
        }
        if matches!(name, "ASC" | "NOTNUMERIC") {
            self.expect(TokenKind::LeftParen, "'(' after method")?;
            self.expect(TokenKind::RightParen, "')' after zero-argument method")?;
            self.count_expression_node(nodes, location)?;
            return Ok(Expr::Call(name.into(), vec![value]));
        }
        if !matches!(
            name,
            "LENGTH" | "YEAR" | "MONTH" | "DAY" | "ISEMPTY" | "HOUR" | "MINUTE" | "SECOND"
        ) {
            return Err(method_location.error(
                "unsupported method; expected Length(), Year(), Month(), Day(), or IsEmpty()",
            ));
        }
        self.expect(TokenKind::LeftParen, "'(' after method name")?;
        self.expect(TokenKind::RightParen, "')' after zero-argument method")?;
        self.count_expression_node(nodes, location)?;
        if name == "LENGTH" {
            Ok(Expr::TextLength(Box::new(value)))
        } else {
            Ok(Expr::DateMethod(name.to_owned(), Box::new(value)))
        }
    }

    fn parse_atom(&mut self, depth: usize, nodes: &mut usize) -> Result<Expr, String> {
        self.ensure_expression_depth(depth)?;
        let token = self.current().clone();
        match token.kind {
            TokenKind::Integer(digits) => {
                self.take();
                let value = digits.parse::<i64>().map_err(|_| {
                    token
                        .location
                        .error("integer literal is outside the i64 range")
                })?;
                self.count_expression_node(nodes, token.location)?;
                Ok(Expr::Integer(value))
            }
            TokenKind::Decimal(digits) => {
                digits
                    .parse::<crate::types::Decimal>()
                    .map_err(|error| token.location.error(error))?;
                self.take();
                self.count_expression_node(nodes, token.location)?;
                Ok(Expr::Decimal(digits))
            }
            TokenKind::Identifier(ref name) if name == "TRUE" || name == "FALSE" => {
                let value = name == "TRUE";
                self.take();
                self.count_expression_node(nodes, token.location)?;
                Ok(Expr::Boolean(value))
            }
            TokenKind::Text(value) => {
                self.take();
                self.count_expression_node(nodes, token.location)?;
                Ok(Expr::Text(value))
            }
            TokenKind::Variable(name) => {
                self.count_expression_node(nodes, token.location)?;
                self.take();
                Ok(Expr::Variable(name))
            }
            TokenKind::Identifier(name) => {
                self.take();
                if self.at(&TokenKind::Dot)
                    && self
                        .tokens
                        .get(self.cursor + 1)
                        .is_some_and(|t| matches!(&t.kind, TokenKind::Identifier(m) if m == "UDP"))
                    && self
                        .tokens
                        .get(self.cursor + 2)
                        .is_some_and(|t| t.kind == TokenKind::LeftParen)
                {
                    self.take();
                    self.take();
                }
                if self.at(&TokenKind::LeftParen) {
                    self.take();
                    self.parse_call(name, depth + 1, nodes, token.location)
                } else if self.allow_attributes {
                    self.count_expression_node(nodes, token.location)?;
                    Ok(Expr::Attribute(name))
                } else {
                    Err(token.location.error(format_args!(
                        "bare identifier {name} is not an expression; use a variable or call"
                    )))
                }
            }
            TokenKind::LeftParen => {
                self.take();
                let expression = self.parse_or(depth + 1, nodes)?;
                self.expect(TokenKind::RightParen, "')' to close the expression")?;
                if KEEP_PARENTHESES.with(std::cell::Cell::get) {
                    return Ok(Expr::Paren(Box::new(expression)));
                }
                Ok(expression)
            }
            _ => Err(token
                .location
                .error("expected an integer, variable, call, or parenthesized expression")),
        }
    }

    fn parse_call(
        &mut self,
        name: String,
        depth: usize,
        nodes: &mut usize,
        location: Location,
    ) -> Result<Expr, String> {
        self.ensure_expression_depth(depth)?;
        let is_formula = matches!(
            name.to_ascii_uppercase().as_str(),
            "SUM" | "COUNT" | "MAX" | "MIN" | "FIND" | "AVERAGE" | "AVG"
        );
        let prev_allow_attributes = self.allow_attributes;
        if is_formula {
            self.allow_attributes = true;
        }

        let result = self.parse_call_inner(name, depth, nodes, location);
        self.allow_attributes = prev_allow_attributes;
        result
    }

    fn parse_call_inner(
        &mut self,
        name: String,
        depth: usize,
        nodes: &mut usize,
        location: Location,
    ) -> Result<Expr, String> {
        let mut arguments = Vec::new();
        if self.at(&TokenKind::RightParen) {
            self.count_expression_node(nodes, location)?;
            self.take();
            return Ok(Expr::Call(name, arguments));
        }
        if self.at(&TokenKind::Star) {
            self.count_expression_node(nodes, location)?;
            self.take();
            self.expect(TokenKind::RightParen, "')' after '*' in call")?;
            return Ok(Expr::Call(name, vec![Expr::Attribute("*".into())]));
        }

        loop {
            arguments.push(self.parse_or(depth, nodes)?);
            if self.at(&TokenKind::RightParen) {
                self.count_expression_node(nodes, location)?;
                self.take();
                return Ok(Expr::Call(name, arguments));
            }
            if !self.at(&TokenKind::Comma) {
                return Err(self
                    .current()
                    .location
                    .error("expected ',' or ')' after a call argument"));
            }
            self.take();
            if self.at(&TokenKind::RightParen) {
                return Err(self
                    .current()
                    .location
                    .error("trailing commas are unsupported in call arguments"));
            }
        }
    }

    fn count_expression_node(&self, nodes: &mut usize, location: Location) -> Result<(), String> {
        *nodes += 1;
        if *nodes > MAX_EXPRESSION_NODES {
            Err(location.error(format_args!(
                "expression exceeds the {MAX_EXPRESSION_NODES}-node limit"
            )))
        } else {
            Ok(())
        }
    }

    fn ensure_expression_depth(&self, depth: usize) -> Result<(), String> {
        if depth > MAX_NESTING {
            Err(self.current().location.error(format_args!(
                "expression nesting exceeds the {MAX_NESTING}-level limit"
            )))
        } else {
            Ok(())
        }
    }
}

/// Parse one procedure body into the shared portable-engine AST.
///
/// Identifiers are normalized to ASCII uppercase; variable names omit `&`.
/// Errors always include a one-based source line and column.
pub fn parse_body(source: &str) -> Result<Vec<Stmt>, String> {
    let stripped = strip_block_comments(source)?;
    let tokens = Lexer::new(&stripped)?.lex()?;
    Parser::new(tokens).parse_body()
}

thread_local! {
    static UNREACHABLE: std::cell::RefCell<Option<Vec<(usize, usize)>>> =
        const { std::cell::RefCell::new(None) };
}

/// One-based `(line, column)` pairs.
pub type SourcePositions = Vec<(usize, usize)>;

fn record_unreachable(location: Location) {
    UNREACHABLE.with(|log| {
        if let Some(log) = log.borrow_mut().as_mut() {
            log.push((location.line, location.column));
        }
    });
}

/// Like `parse_body`, and also returns the one-based `(line, column)` of the first statement of each block
/// that follows a `Return` or `Exit` of the same block: code that can never execute. A subroutine
/// definition after the `Return` is not code that runs in line, so it is not reported. Only the sequence
/// inside one block is judged; a `Return` under a condition ends nothing outside its own block. The
/// positions are in source order of completion of their blocks, not sorted.
pub fn parse_body_with_unreachable(source: &str) -> Result<(Vec<Stmt>, SourcePositions), String> {
    let previous = UNREACHABLE.with(|log| log.replace(Some(Vec::new())));
    let result = parse_body(source);
    let found = UNREACHABLE
        .with(|log| log.replace(previous))
        .unwrap_or_default();
    result.map(|body| (body, found))
}

/// Replace each `/* ... */` block comment with spaces, keeping its line breaks, so every later token
/// keeps its real line and column. The comment does not nest: the first `*/` closes it. A delimiter
/// inside a string literal is text, and a `//` line comment is left for the lexer. An unclosed comment
/// is refused with the line where it opens.
fn strip_block_comments(source: &str) -> Result<String, String> {
    let mut out = String::with_capacity(source.len());
    let mut rest = source;
    while let Some(ch) = rest.chars().next() {
        if ch == '/' && rest[1..].starts_with('/') {
            let end = rest.find(['\r', '\n']).unwrap_or(rest.len());
            out.push_str(&rest[..end]);
            rest = &rest[end..];
        } else if ch == '/' && rest[1..].starts_with('*') {
            let line = source[..source.len() - rest.len()].matches('\n').count() + 1;
            let Some(close) = rest[2..].find("*/") else {
                return Err(format!("line {line}, column 1: unclosed block comment"));
            };
            let body = &rest[..close + 4];
            for c in body.chars() {
                out.push(if c == '\r' || c == '\n' { c } else { ' ' });
            }
            rest = &rest[close + 4..];
        } else if ch == '\'' || ch == '"' {
            let end = rest[1..].find(ch).map_or(rest.len(), |i| i + 2);
            out.push_str(&rest[..end]);
            rest = &rest[end..];
        } else {
            out.push(ch);
            rest = &rest[ch.len_utf8()..];
        }
    }
    Ok(out)
}

/// Parse an individual expression into the shared portable-engine AST.
pub fn parse_expression(source: &str) -> Result<Expr, String> {
    let tokens = Lexer::new(source)?.lex()?;
    let mut parser = Parser::new(tokens);
    let expr = parser.parse_expression()?;
    parser.expect(TokenKind::Eof, "end of input after expression")?;
    Ok(expr)
}

/// Parse an individual expression with attributes admitted (for formula strings).
pub fn parse_expression_with_attributes(source: &str) -> Result<Expr, String> {
    let tokens = Lexer::new(source)?.lex()?;
    let mut parser = Parser::new(tokens);
    parser.allow_attributes = true;
    let expr = parser.parse_expression()?;
    parser.expect(TokenKind::Eof, "end of input after expression")?;
    Ok(expr)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn parses_assignment_calls_and_operator_precedence() {
        let body = parse_body("&result = Sum(&left, double(2 + 3 * 4))\nReturn &result").unwrap();
        assert_eq!(
            body,
            vec![
                Stmt::Assign(
                    "RESULT".into(),
                    Expr::Call(
                        "SUM".into(),
                        vec![
                            Expr::Variable("LEFT".into()),
                            Expr::Call(
                                "DOUBLE".into(),
                                vec![Expr::Binary(
                                    Box::new(Expr::Integer(2)),
                                    BinOp::Add,
                                    Box::new(Expr::Binary(
                                        Box::new(Expr::Integer(3)),
                                        BinOp::Multiply,
                                        Box::new(Expr::Integer(4)),
                                    )),
                                )],
                            ),
                        ],
                    ),
                ),
                Stmt::Return(Expr::Variable("RESULT".into())),
            ]
        );
    }

    #[test]
    fn parses_control_flow_comments_semicolons_and_crlf() {
        let body = parse_body(
            "// comment\r\nIF &flag <> 0; Print(&flag)\r\nELSE\r\nDo While &n > 0\n&n = &n - 1; EndDo\r\nEndIf",
        )
        .unwrap();
        assert_eq!(
            body,
            vec![Stmt::If(
                Expr::Binary(
                    Box::new(Expr::Variable("FLAG".into())),
                    BinOp::NotEqual,
                    Box::new(Expr::Integer(0)),
                ),
                vec![Stmt::Print(Expr::Variable("FLAG".into()))],
                vec![Stmt::While(
                    Expr::Binary(
                        Box::new(Expr::Variable("N".into())),
                        BinOp::Greater,
                        Box::new(Expr::Integer(0)),
                    ),
                    vec![Stmt::Assign(
                        "N".into(),
                        Expr::Binary(
                            Box::new(Expr::Variable("N".into())),
                            BinOp::Subtract,
                            Box::new(Expr::Integer(1)),
                        ),
                    )],
                )],
            )]
        );
    }

    #[test]
    fn parses_all_comparisons_and_signed_i64_boundary() {
        for (source, op) in [
            ("&x = 1 = 1", BinOp::Equal),
            ("&x = 1 <> 2", BinOp::NotEqual),
            ("&x = 1 < 2", BinOp::Less),
            ("&x = 1 <= 2", BinOp::LessEqual),
            ("&x = 2 > 1", BinOp::Greater),
            ("&x = 2 >= 1", BinOp::GreaterEqual),
        ] {
            let parsed = parse_body(source).unwrap();
            assert!(matches!(
                parsed.as_slice(),
                [Stmt::Assign(_, Expr::Binary(_, found, _))] if *found == op
            ));
        }
        assert_eq!(
            parse_body("Return -9223372036854775808").unwrap(),
            vec![Stmt::Return(Expr::Integer(i64::MIN))]
        );
        assert_eq!(
            parse_body("Return 9223372036854775807").unwrap(),
            vec![Stmt::Return(Expr::Integer(i64::MAX))]
        );
    }

    #[test]
    fn parses_void_returns_at_statement_end_and_preserves_value_returns() {
        assert_eq!(
            parse_body("Return\nReturn 7").unwrap(),
            vec![Stmt::ReturnVoid, Stmt::Return(Expr::Integer(7))]
        );
        assert_eq!(parse_body("Return").unwrap(), vec![Stmt::ReturnVoid]);
        assert_eq!(
            parse_body("Return; Print(1)").unwrap(),
            vec![Stmt::ReturnVoid, Stmt::Print(Expr::Integer(1))]
        );
        assert_eq!(parse_body("Return;").unwrap(), vec![Stmt::ReturnVoid]);
    }

    #[test]
    fn permits_empty_bodies_and_zero_argument_calls() {
        assert_eq!(parse_body("").unwrap(), Vec::<Stmt>::new());
        assert_eq!(
            parse_body("Return NoArgs()").unwrap(),
            vec![Stmt::Return(Expr::Call("NOARGS".into(), vec![]))]
        );
    }

    #[test]
    fn parses_call_statements_in_both_forms() {
        let call = Stmt::Call(Expr::Call(
            "PROC".into(),
            vec![Expr::Variable("A".into()), Expr::Integer(1)],
        ));
        assert_eq!(parse_body("Proc(&a, 1)").unwrap(), vec![call.clone()]);
        assert_eq!(parse_body("proc.call(&a, 1)").unwrap(), vec![call]);
    }

    #[test]
    fn rejects_unsupported_constructs_and_malformed_statements_with_locations() {
        for (source, expected) in [
            ("For Each", "expected EndFor"),
            ("&x = \"text", "unterminated text literal"),
            ("&x = 1.", "decimal point requires fractional digits"),
            ("&x = unknown", "bare identifier"),
            ("CallProc", "unsupported statement"),
            ("CallProc.Run()", "expected keyword CALL"),
            ("&x = 1\nElse", "unexpected block terminator Else"),
            ("If 1\nReturn 1", "expected EndIf or Else"),
            ("If 1\nElse\nElse\nEndIf", "expected EndIf"),
            ("Do While 1\nEndIf", "expected EndDo"),
            ("&x = 1 < 2 < 3", "chained comparisons are unsupported"),
            ("&x = 9223372036854775808", "outside the i64 range"),
            ("&x = 1\n  @", "line 2, column 3"),
            ("If 1 Return 1 EndIf", "expected a newline or ';'"),
        ] {
            let error = parse_body(source).unwrap_err();
            assert!(error.contains(expected), "{source:?}: {error}");
            assert!(
                error.contains("line ") && error.contains("column "),
                "{error}"
            );
        }
    }

    #[test]
    fn rejects_incomplete_calls_and_invalid_variable_references() {
        for source in ["Return Sum(1,)", "Return Sum(1", "& = 1", "&9name = 1"] {
            let error = parse_body(source).unwrap_err();
            assert!(
                error.contains("line ") && error.contains("column "),
                "{error}"
            );
        }
    }

    #[test]
    fn rejects_case_chains_that_exceed_lowered_nesting_limit() {
        let cases = (0..=MAX_NESTING)
            .map(|index| format!("Case {index}\n"))
            .collect::<String>();
        let error = parse_body(&format!("Do Case\n{cases}EndCase")).unwrap_err();
        assert!(error.contains("block nesting exceeds"), "{error}");
    }

    #[test]
    fn rejects_nested_control_flow_in_late_case_bodies() {
        let preceding_cases = (0..(MAX_NESTING - 8))
            .map(|index| format!("Case {index}\n"))
            .collect::<String>();
        for (opening, closing) in [
            ("If 1\n", "EndIf\n"),
            ("Do While 1\n", "EndDo\n"),
            ("Do Case\nCase 1\n", "EndCase\n"),
        ] {
            let mut source = format!("Do Case\n{preceding_cases}Case 999\n");
            source.push_str(&opening.repeat(8));
            source.push_str("Return 1\n");
            source.push_str(&closing.repeat(8));
            source.push_str("EndCase");

            let error = parse_body(&source).unwrap_err();
            assert!(error.contains("block nesting exceeds"), "{error}");
        }
    }

    #[test]
    fn rejects_otherwise_body_beyond_lowered_nesting_limit() {
        let cases = (0..(MAX_NESTING - 1))
            .map(|index| format!("Case {index}\n"))
            .collect::<String>();
        let source = format!("Do Case\n{cases}Otherwise\nReturn 1\nEndCase");
        let error = parse_body(&source).unwrap_err();
        assert!(error.contains("block nesting exceeds"), "{error}");
    }

    #[test]
    fn a_body_as_large_as_the_catalog_admits_is_parsed_not_refused_at_one_mebibyte() {
        // A real statement repeated past 1 MiB: the lexer's byte bound is the catalog's (8 MiB), not 1 MiB.
        let line = "Msg('x', status)
";
        let body = line.repeat((1024 * 1024 + 1) / line.len() + 1);
        assert!(body.len() > 1024 * 1024 && body.len() < MAX_SOURCE_BYTES);
        // The token limit may refuse it (a statement repeated past 1 MiB is many tokens); the byte bound must not.
        let refused = parse_body(&body).err();
        assert!(
            refused
                .as_deref()
                .is_none_or(|e| !e.contains("-byte limit")),
            "a 1 MiB+ body is refused by the byte bound: {refused:?}"
        );
    }

    #[test]
    fn a_body_past_the_catalog_bound_is_refused_by_the_byte_bound() {
        let error = parse_body(&" ".repeat(MAX_SOURCE_BYTES + 1)).unwrap_err();
        assert!(
            error.contains("source exceeds the 8388608-byte limit"),
            "{error}"
        );
    }

    #[test]
    fn enforces_source_token_and_nesting_limits() {
        assert!(
            parse_body(&" ".repeat(MAX_SOURCE_BYTES + 1))
                .unwrap_err()
                .contains("source exceeds")
        );
        assert!(
            parse_body(&";".repeat(MAX_TOKENS + 1))
                .unwrap_err()
                .contains("token limit")
        );

        let nested_if = format!(
            "{}Return 1{}",
            "If 1\n".repeat(MAX_NESTING + 1),
            "\nEndIf".repeat(MAX_NESTING + 1)
        );
        assert!(
            parse_body(&nested_if)
                .unwrap_err()
                .contains("block nesting exceeds")
        );

        let nested_expr = format!(
            "Return {}1{}",
            "(".repeat(MAX_NESTING + 2),
            ")".repeat(MAX_NESTING + 2)
        );
        assert!(
            parse_body(&nested_expr)
                .unwrap_err()
                .contains("expression nesting exceeds")
        );

        let boundary_expression =
            format!("Return -(1){}", "+1".repeat((MAX_EXPRESSION_NODES - 2) / 2));
        assert_eq!(
            parse_body(&boundary_expression).unwrap().len(),
            1,
            "an expression at the node limit must remain valid"
        );

        let operator_chain = format!("Return 1{}", "+1".repeat(5_000));
        assert!(
            parse_body(&operator_chain)
                .unwrap_err()
                .contains("node limit")
        );

        let nested_calls = format!(
            "Return Root({})",
            std::iter::repeat_n("Leaf(1)", MAX_EXPRESSION_NODES / 2)
                .collect::<Vec<_>>()
                .join(",")
        );
        assert!(
            parse_body(&nested_calls)
                .unwrap_err()
                .contains("node limit")
        );
    }

    #[test]
    fn parses_for_each_with_wheres_orders_defined_by_and_when_none() {
        let source = r#"
            For Each CustomerId, CustomerName
                Where CustomerActive = true
                Where CustomerId >= 100
                Order CustomerName Desc, CustomerId
                Defined By CustomerId
                &Total = &Total + 1
            When None
                &Total = 0
            EndFor
        "#;
        let stmts = parse_body(source).unwrap();
        assert_eq!(stmts.len(), 1);
        let Stmt::ForEach(for_each) = &stmts[0] else {
            panic!("expected ForEach statement");
        };
        assert_eq!(
            for_each.explicit_attributes,
            vec!["CUSTOMERID", "CUSTOMERNAME"]
        );
        assert_eq!(for_each.wheres.len(), 2);
        assert_eq!(for_each.orders.len(), 2);
        assert_eq!(for_each.orders[0].attribute, "CUSTOMERNAME");
        assert!(for_each.orders[0].descending);
        assert_eq!(for_each.orders[1].attribute, "CUSTOMERID");
        assert!(!for_each.orders[1].descending);
        assert_eq!(for_each.defined_by.as_deref(), Some("CUSTOMERID"));
        assert_eq!(for_each.body.len(), 1);
        assert_eq!(for_each.when_none.len(), 1);
    }
}
