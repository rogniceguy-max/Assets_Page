use serde::Serialize;
use serde_json::{Map, Value};
use thiserror::Error;

#[derive(Debug, Error)]
pub enum CanonicalJsonError {
    #[error("value cannot be serialized as canonical JSON: {0}")]
    Serialize(#[from] serde_json::Error),
}

pub fn canonical_json<T: Serialize>(value: &T) -> Result<Vec<u8>, CanonicalJsonError> {
    let value = serde_json::to_value(value)?;
    let sorted = sort_value(value);
    Ok(serde_json::to_vec(&sorted)?)
}

pub fn canonical_json_without<T: Serialize>(
    value: &T,
    omitted_key: &str,
) -> Result<Vec<u8>, CanonicalJsonError> {
    let mut value = serde_json::to_value(value)?;
    if let Value::Object(object) = &mut value {
        object.remove(omitted_key);
    }
    Ok(serde_json::to_vec(&sort_value(value))?)
}

fn sort_value(value: Value) -> Value {
    match value {
        Value::Array(items) => Value::Array(items.into_iter().map(sort_value).collect()),
        Value::Object(object) => {
            let mut entries: Vec<_> = object.into_iter().collect();
            entries.sort_by(|left, right| left.0.cmp(&right.0));
            let mut sorted = Map::new();
            for (key, value) in entries {
                sorted.insert(key, sort_value(value));
            }
            Value::Object(sorted)
        }
        scalar => scalar,
    }
}
