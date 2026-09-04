//! Stub derive macros for the TerraScale Rust SDK.
//!
//! `#[derive(Schema)]` implements `terrascale::schema::Document` for a struct,
//! using the struct name (lowercased) as the collection name and an empty
//! field list. Field-level mapping from `terrascale push` / codegen arrives
//! with the pinned contract and replaces this stub.

use proc_macro::TokenStream;
use quote::quote;
use syn::{parse_macro_input, DeriveInput};

/// Derive `terrascale::schema::Document` (stub: collection name only).
#[proc_macro_derive(Schema)]
pub fn derive_schema(input: TokenStream) -> TokenStream {
    let ast = parse_macro_input!(input as DeriveInput);
    let name = &ast.ident;
    let collection = name.to_string().to_lowercase();
    let expanded = quote! {
        impl ::terrascale::schema::Document for #name {
            fn collection() -> &'static str {
                #collection
            }

            fn schema() -> ::terrascale::schema::CollectionSchema {
                ::terrascale::schema::CollectionSchema::new(Self::collection())
            }
        }
    };
    TokenStream::from(expanded)
}
