//! Private index declarations reserved for the W3-2 query/index lane.

#![allow(dead_code)]

use std::marker::PhantomData;

struct IndexSegment(PhantomData<()>);
struct IndexDelta(PhantomData<()>);
struct IndexBuilder(PhantomData<()>);
struct IndexLock(PhantomData<()>);
