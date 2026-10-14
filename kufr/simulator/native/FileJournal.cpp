#include "FileJournal.h"
#include <cerrno>
#include <cstdio>
#include <fstream>
#include <stdexcept>

#ifdef _WIN32
#include <windows.h>
#else
#include <fcntl.h>
#include <sys/file.h>
#include <unistd.h>
#endif

FileJournal::FileJournal(const std::string& path) : path_(path) {
  // Never allow two control owners to mutate the same persistent identity.
#ifdef _WIN32
  HANDLE handle = CreateFileA((path_ + ".lock").c_str(), GENERIC_READ | GENERIC_WRITE,
      0, nullptr, OPEN_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
  if (handle == INVALID_HANDLE_VALUE) throw std::runtime_error("journal-lock-unavailable");
  lock_ = reinterpret_cast<intptr_t>(handle);
#else
  const int descriptor = open((path_ + ".lock").c_str(), O_CREAT | O_RDWR, 0600);
  if (descriptor < 0 || flock(descriptor, LOCK_EX | LOCK_NB) != 0) {
    if (descriptor >= 0) close(descriptor);
    throw std::runtime_error("journal-lock-unavailable");
  }
  lock_ = descriptor;
#endif
}

FileJournal::~FileJournal() {
#ifdef _WIN32
  if (lock_ != -1) CloseHandle(reinterpret_cast<HANDLE>(lock_));
#else
  if (lock_ >= 0) close(static_cast<int>(lock_));
#endif
}

JournalReadResult FileJournal::Read(uint8_t* data, size_t capacity, size_t& length) {
  length = 0;
  std::ifstream file(path_, std::ios::binary);
  if (!file) {
    // Distinguish genuinely absent storage from unreadable existing files.
    std::ifstream probe(path_ + ".tmp", std::ios::binary);
    if (probe) return JournalReadResult::Failed;
#ifdef _WIN32
    return GetFileAttributesA(path_.c_str()) == INVALID_FILE_ATTRIBUTES &&
        GetLastError() == ERROR_FILE_NOT_FOUND ? JournalReadResult::Missing : JournalReadResult::Failed;
#else
    return access(path_.c_str(), F_OK) != 0 && errno == ENOENT
        ? JournalReadResult::Missing : JournalReadResult::Failed;
#endif
  }
  file.read(reinterpret_cast<char*>(data), static_cast<std::streamsize>(capacity));
  length = static_cast<size_t>(file.gcount());
  if (file.bad() || file.peek() != std::char_traits<char>::eof()) return JournalReadResult::Failed;
  return JournalReadResult::Found;
}

bool FileJournal::Write(const uint8_t* data, size_t length) {
  if (failWrites) return false;
  const std::string temporary = path_ + ".tmp";
#ifdef _WIN32
  HANDLE file = CreateFileA(temporary.c_str(), GENERIC_WRITE, 0, nullptr,
      CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
  if (file == INVALID_HANDLE_VALUE) return false;
  DWORD written = 0;
  const bool success = WriteFile(file, data, static_cast<DWORD>(length), &written, nullptr) &&
      written == length && FlushFileBuffers(file);
  CloseHandle(file);
  if (!success) return false;
  return MoveFileExA(temporary.c_str(), path_.c_str(), MOVEFILE_REPLACE_EXISTING | MOVEFILE_WRITE_THROUGH) != 0;
#else
  const int file = open(temporary.c_str(), O_CREAT | O_TRUNC | O_WRONLY, 0600);
  if (file < 0) return false;
  size_t written = 0;
  while (written < length) {
    const ssize_t count = write(file, data + written, length - written);
    if (count <= 0) { close(file); return false; }
    written += static_cast<size_t>(count);
  }
  const bool success = fsync(file) == 0;
  const bool closed = close(file) == 0;
  if (!success || !closed || rename(temporary.c_str(), path_.c_str()) != 0) return false;
  const size_t slash = path_.find_last_of('/');
  const std::string directory = slash == std::string::npos ? "." :
      slash == 0 ? "/" : path_.substr(0, slash);
  const int parent = open(directory.c_str(), O_RDONLY | O_DIRECTORY);
  if (parent < 0) return false;
  const bool durable = fsync(parent) == 0;
  close(parent);
  return durable;
#endif
}
