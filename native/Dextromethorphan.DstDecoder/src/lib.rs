use dst_decoder::decoder::DstDecoder;
use std::ffi::c_char;
use std::panic::{AssertUnwindSafe, catch_unwind};
use std::ptr;

const API_VERSION: u32 = 1;

#[unsafe(no_mangle)]
pub extern "C" fn dext_dst_api_version() -> u32 {
    API_VERSION
}

#[unsafe(no_mangle)]
pub unsafe extern "C" fn dext_dst_create(
    channels: u32,
    sample_rate: u32,
    frame_bytes: *mut usize,
    error: *mut c_char,
    error_capacity: usize,
) -> *mut DstDecoder {
    if frame_bytes.is_null() {
        unsafe { write_error(error, error_capacity, "frame_bytes is null") };
        return ptr::null_mut();
    }

    match catch_unwind(|| DstDecoder::new(channels as usize, sample_rate as usize)) {
        Ok(Ok(decoder)) => {
            unsafe { *frame_bytes = decoder.dsd_frame_bytes() };
            Box::into_raw(Box::new(decoder))
        }
        Ok(Err(value)) => {
            unsafe { write_error(error, error_capacity, &value.to_string()) };
            ptr::null_mut()
        }
        Err(_) => {
            unsafe { write_error(error, error_capacity, "DST decoder panicked during initialization") };
            ptr::null_mut()
        }
    }
}

#[unsafe(no_mangle)]
pub unsafe extern "C" fn dext_dst_decode(
    decoder: *mut DstDecoder,
    input: *const u8,
    input_length: usize,
    output: *mut u8,
    output_length: usize,
    error: *mut c_char,
    error_capacity: usize,
) -> isize {
    if decoder.is_null() || input.is_null() || output.is_null() {
        unsafe { write_error(error, error_capacity, "decoder or buffer is null") };
        return -1;
    }

    let decoder = unsafe { &mut *decoder };
    let input = unsafe { std::slice::from_raw_parts(input, input_length) };
    let output = unsafe { std::slice::from_raw_parts_mut(output, output_length) };
    match catch_unwind(AssertUnwindSafe(|| decoder.decode_frame(input, output))) {
        Ok(Ok(written)) => written as isize,
        Ok(Err(value)) => {
            unsafe { write_error(error, error_capacity, &value.to_string()) };
            -1
        }
        Err(_) => {
            unsafe { write_error(error, error_capacity, "DST decoder panicked while reading the frame") };
            -1
        }
    }
}

#[unsafe(no_mangle)]
pub unsafe extern "C" fn dext_dst_destroy(decoder: *mut DstDecoder) {
    if !decoder.is_null() {
        drop(unsafe { Box::from_raw(decoder) });
    }
}

unsafe fn write_error(target: *mut c_char, capacity: usize, message: &str) {
    if target.is_null() || capacity == 0 {
        return;
    }
    let bytes = message.as_bytes();
    let count = bytes.len().min(capacity - 1);
    unsafe {
        ptr::copy_nonoverlapping(bytes.as_ptr(), target.cast::<u8>(), count);
        *target.add(count) = 0;
    }
}
