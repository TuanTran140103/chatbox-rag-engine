-- DATA STRUCTURE:
-- Key: "genqa:model:{modelId}" (Hash)
-- Field: {documentId}
-- Value: { "allowSlot": int, "used": int, "remainingWork": int, "totalWork": int }
--
-- Input:
-- KEYS[1]: model_key
-- ARGV[1]: documentId (workerId)
--
-- Returns: JSON string của worker data sau khi tăng used
--          nil  nếu worker không tồn tại (đã bị cancel/expired)
--          error "No slots available" nếu quota tạm thời đã đầy

local model_key = KEYS[1]
local worker_id = ARGV[1]

-- 1. Đọc global total_max
local total_max_raw = redis.call('HGET', model_key, '__config_total_max')
if not total_max_raw then return nil end
local total_max = tonumber(total_max_raw)

-- 2. Lấy dữ liệu job hiện tại
local raw = redis.call('HGET', model_key, worker_id)
if not raw then return nil end
local data = cjson.decode(raw)

-- 3. Local check: job này còn slot trong quota của mình không?
if data.used >= data.allowSlot then
    return redis.error_reply("No slots available")
end

-- 4. Global check: tổng used của tất cả job có vượt total_max không?
local all_raw = redis.call('HGETALL', model_key)
local global_used = 0
for i = 1, #all_raw, 2 do
    local field = all_raw[i]
    if field ~= "__config_total_max" then
        local w_data = cjson.decode(all_raw[i+1])
        global_used = global_used + (w_data.used or 0)
    end
end

if global_used >= total_max then
    return redis.error_reply("No slots available")
end

-- 5. Tăng used và lưu
data.used = data.used + 1
redis.call('HSET', model_key, worker_id, cjson.encode(data))

return cjson.encode(data)
