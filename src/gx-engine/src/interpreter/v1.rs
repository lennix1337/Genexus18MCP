//! `format_version` 1: untyped `i64` Procedures (`backend::emit`). Same semantics as the generated
//! program: checked integer arithmetic, `0`/`1` truth values, locals start at 0, a Procedure that
//! reaches its end returns 0, and one execution budget ticks at each call and each loop iteration.

use crate::frontend::{BinOp, Expr, Procedure, Stmt};
use std::{collections::HashMap, io::Write, time::Instant};

type R<T> = Result<T, String>;

const MAX_CALL_DEPTH: u32 = 128;

pub(super) struct Machine<'p, 'o> {
    pub(super) parsed: &'p [Procedure],
    pub(super) steps: u64,
    pub(super) deadline: Instant,
    pub(super) ticks: u32,
    pub(super) out: &'o mut dyn Write,
}

enum Flow {
    Next,
    Return(i64),
}

struct Frame {
    locals: HashMap<String, i64>,
    depth: u32,
}

fn key(name: &str) -> String {
    name.to_ascii_uppercase()
}

impl Machine<'_, '_> {
    fn tick(&mut self) -> R<()> {
        self.steps = self
            .steps
            .checked_sub(1)
            .ok_or("execution step limit exceeded")?;
        self.ticks = self.ticks.wrapping_add(1);
        if self.ticks % 256 == 0 && Instant::now() >= self.deadline {
            return Err("execution exceeded the wall-clock deadline".into());
        }
        Ok(())
    }

    pub(super) fn call(&mut self, index: usize, depth: u32, args: Vec<i64>) -> R<i64> {
        if depth >= MAX_CALL_DEPTH {
            return Err("procedure call depth limit exceeded".into());
        }
        self.tick()?;
        let procedure = &self.parsed[index];
        let mut frame = Frame {
            locals: HashMap::new(),
            depth,
        };
        for (parameter, value) in procedure.params.iter().zip(args) {
            frame.locals.insert(key(parameter), value);
        }
        // Every assignment target is a local initialised to 0.
        declare(&procedure.body, &mut frame.locals);
        let parsed = self.parsed;
        match self.block(&mut frame, &parsed[index].body)? {
            Flow::Return(value) => Ok(value),
            Flow::Next => Ok(0),
        }
    }

    fn block(&mut self, frame: &mut Frame, body: &[Stmt]) -> R<Flow> {
        for statement in body {
            match statement {
                Stmt::Assign(name, expression) => {
                    let value = self.expression(frame, expression)?;
                    frame.locals.insert(key(name), value);
                }
                Stmt::Print(expression) => {
                    let value = self.expression(frame, expression)?;
                    writeln!(self.out, "{value}")
                        .map_err(|e| format!("cannot write output: {e}"))?;
                }
                Stmt::Return(expression) => {
                    return Ok(Flow::Return(self.expression(frame, expression)?));
                }
                Stmt::If(condition, yes, no) => {
                    let branch = if self.expression(frame, condition)? != 0 {
                        yes
                    } else {
                        no
                    };
                    if let Flow::Return(value) = self.block(frame, branch)? {
                        return Ok(Flow::Return(value));
                    }
                }
                Stmt::While(condition, body) => {
                    while self.expression(frame, condition)? != 0 {
                        self.tick()?;
                        if let Flow::Return(value) = self.block(frame, body)? {
                            return Ok(Flow::Return(value));
                        }
                    }
                }
                other => {
                    return Err(format!(
                        "statement {other:?} requires format_version 2 and is not supported by the format 1 interpreter"
                    ));
                }
            }
        }
        Ok(Flow::Next)
    }

    fn expression(&mut self, frame: &mut Frame, expression: &Expr) -> R<i64> {
        match expression {
            Expr::Paren(inner) => self.expression(frame, inner),
            Expr::Integer(value) => Ok(*value),
            Expr::Variable(name) => frame
                .locals
                .get(&key(name))
                .copied()
                .ok_or_else(|| format!("read of undeclared variable {name}")),
            Expr::Not(value) => Ok(i64::from(self.expression(frame, value)? == 0)),
            Expr::Negate(value) => self
                .expression(frame, value)?
                .checked_neg()
                .ok_or_else(|| "integer negation overflow".to_string()),
            Expr::Call(name, arguments) => {
                let index = self
                    .parsed
                    .iter()
                    .position(|p| p.name.eq_ignore_ascii_case(name))
                    .ok_or_else(|| format!("call to unresolved procedure {name}"))?;
                let mut values = Vec::with_capacity(arguments.len());
                for argument in arguments {
                    values.push(self.expression(frame, argument)?);
                }
                self.call(index, frame.depth + 1, values)
            }
            Expr::Binary(left, operator, right) => {
                let left = self.expression(frame, left)?;
                match operator {
                    BinOp::And => {
                        return Ok(i64::from(left != 0 && self.expression(frame, right)? != 0));
                    }
                    BinOp::Or => {
                        return Ok(i64::from(left != 0 || self.expression(frame, right)? != 0));
                    }
                    _ => {}
                }
                let right = self.expression(frame, right)?;
                match operator {
                    BinOp::Add => left
                        .checked_add(right)
                        .ok_or_else(|| "integer addition overflow".to_string()),
                    BinOp::Subtract => left
                        .checked_sub(right)
                        .ok_or_else(|| "integer subtraction overflow".to_string()),
                    BinOp::Multiply => left
                        .checked_mul(right)
                        .ok_or_else(|| "integer multiplication overflow".to_string()),
                    BinOp::Divide => {
                        if right == 0 {
                            return Err("division by zero".into());
                        }
                        left.checked_div(right)
                            .ok_or_else(|| "integer division overflow".to_string())
                    }
                    BinOp::Equal => Ok(i64::from(left == right)),
                    BinOp::NotEqual => Ok(i64::from(left != right)),
                    BinOp::Less => Ok(i64::from(left < right)),
                    BinOp::LessEqual => Ok(i64::from(left <= right)),
                    BinOp::Greater => Ok(i64::from(left > right)),
                    BinOp::GreaterEqual => Ok(i64::from(left >= right)),
                    BinOp::And | BinOp::Or => unreachable!("handled above"),
                }
            }
            other => Err(format!(
                "expression {other:?} requires format_version 2 and is not supported by the format 1 interpreter"
            )),
        }
    }
}

fn declare(body: &[Stmt], locals: &mut HashMap<String, i64>) {
    for statement in body {
        match statement {
            Stmt::Assign(name, _) => {
                locals.entry(key(name)).or_insert(0);
            }
            Stmt::If(_, yes, no) => {
                declare(yes, locals);
                declare(no, locals);
            }
            Stmt::While(_, body) => declare(body, locals),
            _ => {}
        }
    }
}
