#pragma once

#include <string>

#include "LocalSocketAdmission.h"

class LocalControlTransport {
 public:
  virtual ~LocalControlTransport() = default;
  virtual bool Send(const LocalSocketAdmission::Handle& handle,
                    const std::string& json) = 0;
  virtual void Close(const LocalSocketAdmission::Handle& handle) = 0;
};
